<#
.SYNOPSIS
快速切换到 dsh@0.1.0-rc.6 并清理更新状态，用于测试自动更新流程。

.DESCRIPTION
1. 清理 pending-update.json（避免旧的 pending 干扰）
2. 清理 staging 目录（避免旧的构建残留）
3. 清理 runtimes 目录（避免旧的 SelfContained 运行时）
4. 安装 dsh@0.1.0-rc.6（降级）
5. 验证版本

.PARAMETER SkipInstall
跳过 npm install -g（如果已经是 rc.6）

.PARAMETER KeepBuild
保留 staging 目录中的已构建运行时（复用缓存加速）

.EXAMPLE
./scripts/test-update-rc6.ps1
./scripts/test-update-rc6.ps1 -KeepBuild
#>

param(
    [switch]$SkipInstall,
    [switch]$KeepBuild
)

$ErrorActionPreference = "Stop"
$dataDir = "$env:USERPROFILE\.dsh\dsh-launcher"

Write-Host "=== dsh 更新测试环境准备 ===" -ForegroundColor Cyan
Write-Host ""

# 1. 关闭正在运行的 launcher
Write-Host "1. 关闭 launcher..." -ForegroundColor Yellow
$procs = Get-Process -Name "DshWeb" -ErrorAction SilentlyContinue
if ($procs) {
    $procs | Stop-Process -Force
    Start-Sleep -Seconds 2
    Write-Host "   已关闭" -ForegroundColor Green
} else {
    Write-Host "   未运行" -ForegroundColor Gray
}

# 2. 清理更新状态
Write-Host ""
Write-Host "2. 清理更新状态..." -ForegroundColor Yellow

# pending-update.json
$pending = "$dataDir\pending-update.json"
if (Test-Path $pending) {
    Remove-Item $pending -Force
    Write-Host "   已删除 pending-update.json" -ForegroundColor Green
} else {
    Write-Host "   pending-update.json 不存在" -ForegroundColor Gray
}

# staging 目录（根据 -KeepBuild 参数决定是否清理）
$staging = "$dataDir\staging"
if (Test-Path $staging) {
    if ($KeepBuild) {
        Write-Host "   保留 staging 目录（-KeepBuild）" -ForegroundColor Yellow
        # 只清理不完整的构建，保留完整的
        Get-ChildItem "$staging\runtime-build-*" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $dshPkg = Join-Path $_.FullName "node_modules\@deepseek-ai\dsh\package.json"
            if (-not (Test-Path $dshPkg)) {
                Remove-Item $_.FullName -Recurse -Force
                Write-Host "   已删除不完整构建: $($_.Name)" -ForegroundColor Yellow
            } else {
                Write-Host "   保留完整构建: $($_.Name)" -ForegroundColor Green
            }
        }
    } else {
        Remove-Item $staging -Recurse -Force
        Write-Host "   已删除 staging 目录" -ForegroundColor Green
    }
} else {
    Write-Host "   staging 目录不存在" -ForegroundColor Gray
}

# runtimes 目录
$runtimes = "$dataDir\runtimes"
if (Test-Path $runtimes) {
    Remove-Item $runtimes -Recurse -Force
    Write-Host "   已删除 runtimes 目录" -ForegroundColor Green
} else {
    Write-Host "   runtimes 目录不存在" -ForegroundColor Gray
}

# skipped-update.json
$skipped = "$dataDir\skipped-update.json"
if (Test-Path $skipped) {
    Remove-Item $skipped -Force
    Write-Host "   已删除 skipped-update.json" -ForegroundColor Green
} else {
    Write-Host "   skipped-update.json 不存在" -ForegroundColor Gray
}

# 3. 降级到 rc.6
Write-Host ""
if (-not $SkipInstall) {
    Write-Host "3. 降级到 dsh@0.1.0-rc.6..." -ForegroundColor Yellow
    $currentVersion = cmd /c dsh --version 2>&1
    Write-Host "   当前版本: $currentVersion"
    
    if ($currentVersion -eq "0.1.0-rc.6") {
        Write-Host "   已经是 rc.6，跳过安装" -ForegroundColor Green
    } else {
        Write-Host "   正在安装 rc.6（可能需要1-2分钟）..." -ForegroundColor Gray
        npm install -g @deepseek-ai/dsh@0.1.0-rc.6 --no-audit --no-fund --registry=https://registry.npmmirror.com 2>&1 | Out-Null
        
        $newVersion = cmd /c dsh --version 2>&1
        if ($newVersion -eq "0.1.0-rc.6") {
            Write-Host "   降级成功: $newVersion" -ForegroundColor Green
        } else {
            Write-Host "   降级失败: $newVersion" -ForegroundColor Red
            Write-Host "   请手动执行: npm install -g @deepseek-ai/dsh@0.1.0-rc.6 --registry=https://registry.npmmirror.com" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "3. 跳过安装（-SkipInstall）" -ForegroundColor Gray
}

# 4. 验证状态
Write-Host ""
Write-Host "=== 验证状态 ===" -ForegroundColor Cyan
Write-Host "dsh 版本: $(cmd /c dsh --version 2>&1)"
Write-Host "pending: $(if (Test-Path "$dataDir\pending-update.json") { '存在' } else { '不存在' })"
Write-Host "staging: $(if (Test-Path "$dataDir\staging") { '存在' } else { '不存在' })"
Write-Host "runtimes: $(if (Test-Path "$dataDir\runtimes") { '存在' } else { '不存在' })"

# 检查 npm/pnpm 缓存
Write-Host ""
Write-Host "=== 缓存状态 ===" -ForegroundColor Cyan
$npmCache = npm config get cache
$npmCacheSize = (Get-ChildItem $npmCache -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
Write-Host "npm cache: $([math]::Round($npmCacheSize / 1MB, 1))MB"

Write-Host ""
Write-Host "=== 测试准备完成 ===" -ForegroundColor Green
Write-Host "现在可以运行新编译的 launcher 测试自动更新了" -ForegroundColor Green
Write-Host ""
Write-Host "运行方式（从 dsh 外部启动）:" -ForegroundColor Yellow
Write-Host "  E:\dsh-launcher\dist\v2-20260820-135031\DshWeb.exe" -ForegroundColor White
Write-Host ""
Write-Host "提示: 使用 -KeepBuild 参数可保留已构建的运行时，加速后续测试" -ForegroundColor Gray
