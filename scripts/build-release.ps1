<#
.SYNOPSIS
Builds the dsh-launcher Windows release package.

.DESCRIPTION
Publishes the WebView2 shell app as a single-file executable, assembles the
deployable files (exe + native loader + all runtime scripts), creates
dsh-launcher-windows-<version>.zip, builds a per-machine MSI installer (WiX v5) and writes
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
$zipPath = Join-Path $root "$OutputDir\dsh-launcher-windows.zip"  # 占位，Version 解析后重命名

# version: explicit argument, or derived from the latest git tag (strip leading 'v')
if (-not $Version) {
    $tag = git -C $root describe --tags --abbrev=0 2>$null
    $Version = if ($tag) { $tag.TrimStart('v') } else { "0.0.0" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z, got: $Version" }
$msiPath = Join-Path $root "$OutputDir\dsh-launcher-$Version.msi"
# v0.3.1：zip 命名带版本号（与 MSI 一致，多版本并存时可辨识）
$zipPath = Join-Path $root "$OutputDir\dsh-launcher-windows-$Version.zip"
$sumsPath = Join-Path $root "$OutputDir\SHA256SUMS.txt"

# 1. publish single-file exe
Write-Host ">> publishing shell app..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root "src\DshShell") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true -p:Version=$Version -o $publishDir
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
foreach ($script in "start-dsh.vbs", "start-dsh.cmd", "dsh-web.cmd", "uninstall-autostart.cmd", "check-prereq.cmd") {
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

Write-Host ">> publishing prereq checker..."
# Native AOT：前置检查器必须在"机器上根本没有 .NET 运行时"的现场跑起来——它检测的第一件事就是
# .NET 在不在。旧实现是框架依赖的 WPF 单文件（177KB），没装 .NET 10 的机器上 apphost 抢先弹
# "你必须安装 .NET Desktop Runtime"，检查逻辑一行都没执行，随后 MSI 报"程序包有问题"（用户实拍）。
# AOT 产物是自包含原生 exe，不依赖任何共享框架；本机没装 MSVC 工具链时这一步会直接失败——
# 失败就是红灯，绝不退回 --self-contained false 悄悄发一个"需要 .NET 才能检查 .NET"的检查器。
$prereqOut = Join-Path $root "installer\PrereqCheck\out"
dotnet publish (Join-Path $root "installer\PrereqCheck") -c Release -r win-x64 `
    -p:PublishAot=true -p:DebugType=none -o $prereqOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish PrereqCheck (Native AOT) failed" }
$prereqExe = Join-Path $prereqOut "PrereqCheck.exe"
if (-not (Test-Path $prereqExe)) { throw "PrereqCheck.exe not produced at $prereqExe" }
# 原生 AOT 产物必然远大于框架依赖单文件（旧值 177KB）；小于 1MB 说明 AOT 没生效，退回去了。
$prereqSize = (Get-Item $prereqExe).Length
if ($prereqSize -lt 1MB) {
    throw "PrereqCheck.exe is only $prereqSize bytes — AOT 未生效，产物仍是框架依赖（缺 .NET 的机器跑不起来）"
}
Write-Host "   PrereqCheck.exe = $([math]::Round($prereqSize/1MB,2)) MB (Native AOT, 不依赖共享框架)"

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

# 5. zip —— 顶层带版本文件夹（解压得到 dsh-launcher-windows-<version>/，不把文件打散，
# v0.4.0 用户反馈）。
$zipRoot = Join-Path $root "$OutputDir\dsh-launcher-windows-$Version"
if (Test-Path $zipRoot) { Remove-Item $zipRoot -Recurse -Force }
Rename-Item $distDir (Split-Path $zipRoot -Leaf)
Write-Host ">> packaging zip..."
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $zipRoot -DestinationPath $zipPath -Force

# 6. checksums (integrity verification for downloads)
Write-Host ">> writing checksums..."
Get-FileHash $zipPath, $msiPath -Algorithm SHA256 | ForEach-Object {
    "{0}  {1}" -f $_.Hash.ToLower(), (Split-Path $_.Path -Leaf)
} | Set-Content $sumsPath -Encoding ascii

Remove-Item $zipRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ">> done:"
Write-Host "    $zipPath"
Write-Host "    $msiPath"
Write-Host "    $sumsPath"

