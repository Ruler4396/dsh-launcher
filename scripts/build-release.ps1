<#
.SYNOPSIS
Builds the dsh-launcher Windows release package.

.DESCRIPTION
Publishes the WebView2 shell app as a single-file executable, assembles the
deployable files (exe + native loader + all runtime scripts) and creates
dsh-launcher-windows.zip under the output directory.

.EXAMPLE
./scripts/build-release.ps1 -OutputDir dist
#>
param(
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root ".publish-tmp"
$distDir = Join-Path $root "$OutputDir\dsh-launcher-windows"
$zipPath = Join-Path $root "$OutputDir\dsh-launcher-windows.zip"

# 1. publish single-file exe
Write-Host ">> publishing shell app..."
dotnet publish (Join-Path $root "src\DshShell") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true -o $publishDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

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

# 3. zip
Write-Host ">> packaging zip..."
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $distDir "*") -DestinationPath $zipPath -Force
Remove-Item $distDir -Recurse -Force
Remove-Item $publishDir -Recurse -Force

Write-Host ">> done: $zipPath"
