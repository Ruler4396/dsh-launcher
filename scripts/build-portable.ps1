# build-portable.ps1 —— 只构建"免安装版"（ZIP 便携包），跳过 MSI/WiX。
# 与 build-release.ps1 的区别：
#   * 不依赖 WiX v5 / DTF，缺失依赖面更小；
#   * 产物只有 dist\dsh-launcher-windows-<version>.zip + SHA256 校验和。
<#
.SYNOPSIS
Builds the portable (免安装) ZIP release package for dsh-launcher.

.DESCRIPTION
Publishes the WebView2 shell app as a single-file executable, assembles the
deployable files (exe + native loader + runtime scripts) and creates
dsh-launcher-windows-<version>.zip plus a SHA256 checksum file.

.PARAMETER Version
Release version string (default: derived from latest git tag, leading 'v' stripped).
If the nearest tag is not a semver (e.g. refactor/step6), pass -Version explicitly.

.EXAMPLE
pwsh ./scripts/build-portable.ps1 -Version 0.4.0
#>
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root ".publish-tmp"
$distDir = Join-Path $root "dist\dsh-launcher-windows"

if (-not $Version) {
    $tag = git -C $root describe --tags --abbrev=0 2>$null
    if ($tag -match '^v?(\d+\.\d+\.\d+)$') { $Version = $Matches[1] }
}
if (-not $Version) { throw "Version must be x.y.z — pass -Version explicitly (nearest tag is not semver)." }
Write-Host ">> version: $Version"

# 1. publish single-file exe (framework-dependent + self-contained runtime deps as needed)
Write-Host ">> publishing DshShell (win-x64, single-file)..."
dotnet publish (Join-Path $root "src\DshShell") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true `
    -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
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
foreach ($script in "start-dsh.vbs", "start-dsh.cmd", "dsh-web.cmd",
                    "uninstall-autostart.cmd", "check-prereq.cmd") {
    Copy-Item (Join-Path $root "scripts\$script") $distDir
}

# 3. zip
$zipPath = Join-Path $root "dist\dsh-launcher-windows-$Version.zip"
Write-Host ">> packaging zip: $zipPath"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $distDir "*") -DestinationPath $zipPath -Force

# 4. checksum
$sumsPath = Join-Path $root "dist\dsh-launcher-windows-$Version.sha256"
Get-FileHash $zipPath -Algorithm SHA256 | ForEach-Object {
    "{0}  {1}" -f $_.Hash.ToLower(), (Split-Path $_.Path -Leaf)
} | Set-Content $sumsPath -Encoding ascii

Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ">> done: $zipPath"
Write-Host ">> checksum: $sumsPath"
