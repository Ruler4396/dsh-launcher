<#
.SYNOPSIS
Builds the dsh-launcher Windows release package.

.DESCRIPTION
Publishes the WebView2 shell app as a single-file executable, assembles the
deployable files (exe + native loader + all runtime scripts), creates
dsh-launcher-windows.zip, builds a per-machine MSI installer (WiX v5) and writes
SHA256 checksums for both artifacts.

.EXAMPLE
./scripts/build-release.ps1 -OutputDir dist                 # version from latest git tag
./scripts/build-release.ps1 -OutputDir dist -Version 0.1.2  # explicit version
#>
param(
    [string]$OutputDir = "dist",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root ".publish-tmp"
$distDir = Join-Path $root "$OutputDir\dsh-launcher-windows"
$zipPath = Join-Path $root "$OutputDir\dsh-launcher-windows.zip"

# version: explicit argument, or derived from the latest git tag (strip leading 'v')
if (-not $Version) {
    $tag = git -C $root describe --tags --abbrev=0 2>$null
    $Version = if ($tag) { $tag.TrimStart('v') } else { "0.0.0" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z, got: $Version" }
$msiPath = Join-Path $root "$OutputDir\dsh-launcher-$Version.msi"
$sumsPath = Join-Path $root "$OutputDir\SHA256SUMS.txt"

# 1. publish single-file exe
Write-Host ">> publishing shell app..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root "src\DshShell") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path (Join-Path $publishDir "DshWeb.exe")) -or
    -not (Test-Path (Join-Path $publishDir "WebView2Loader.dll"))) {
    throw "publish output incomplete: DshWeb.exe / WebView2Loader.dll missing"
}

# 2. assemble deploy files (exclude pdb / xml docs / runtime user data)
Write-Host ">> assembling release folder..."
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item (Join-Path $publishDir "DshWeb.exe") $distDir
Copy-Item (Join-Path $publishDir "WebView2Loader.dll") $distDir
if (Test-Path (Join-Path $publishDir "runtimes")) {
    Copy-Item (Join-Path $publishDir "runtimes") $distDir -Recurse
}
# ship all runtime scripts so the deploy folder is self-contained
foreach ($script in "start-dsh.vbs", "start-dsh.cmd", "dsh-web.cmd", "uninstall-autostart.cmd") {
    Copy-Item (Join-Path $root "scripts\$script") $distDir
}

# 3. publish the modern folder-picker exe (Type-38, client-process dialog) and
#    build the DTF read-back CA (Type-1, runs in the msiexec CA server; its
#    MsiSetProperty changes sync back to the client UI - verified in logs)
Write-Host ">> publishing folder picker..."
$pickerOut = Join-Path $root "installer\FolderPicker\out"
dotnet publish (Join-Path $root "installer\FolderPicker") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true -o $pickerOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish FolderPicker failed" }

Write-Host ">> building folder picker CA..."
dotnet build (Join-Path $root "installer\FolderPickerCa") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build FolderPickerCa failed" }
if (-not (Test-Path (Join-Path $root "installer\FolderPickerCa\bin\x64\Release\net20\FolderPickerCa.CA.dll"))) {
    throw "FolderPickerCa.CA.dll not produced (DTF build failed?)"
}

# 4. per-machine MSI installer (WiX v5; elevated per-machine uninstall; see installer/product.wxs)
Write-Host ">> building MSI installer..."
$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    dotnet tool install --global wix --version "5.0.2" | Out-Null
    $wix = Get-Command wix -ErrorAction SilentlyContinue
}
if (-not $wix) { throw "WiX tool not available; run: dotnet tool install --global wix --version 5.0.2" }
# the UI extension (install wizard) is not bundled with WiX v5; ensure it is
# installed. `add` is idempotent (exit 0 when already present). Do NOT gate it
# on `extension list`: an empty list comes back as an empty array in
# PowerShell, making `-notmatch` falsy and skipping the install.
& $wix.Source extension add -g WixToolset.UI.wixext/5.0.2 *> $null
if ($LASTEXITCODE -ne 0) { throw "failed to install WixToolset.UI.wixext" }

& $wix.Source build (Join-Path $root "installer\product.wxs") -arch x64 `
    -ext WixToolset.UI.wixext -culture zh-CN `
    -d "ProductVersion=$Version" -d "SourceDir=$distDir" -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

# 5. zip
Write-Host ">> packaging zip..."
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $distDir "*") -DestinationPath $zipPath -Force

# 6. checksums (integrity verification for downloads)
Write-Host ">> writing checksums..."
Get-FileHash $zipPath, $msiPath -Algorithm SHA256 | ForEach-Object {
    "{0}  {1}" -f $_.Hash.ToLower(), (Split-Path $_.Path -Leaf)
} | Set-Content $sumsPath -Encoding ascii

Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ">> done:"
Write-Host "    $zipPath"
Write-Host "    $msiPath"
Write-Host "    $sumsPath"

