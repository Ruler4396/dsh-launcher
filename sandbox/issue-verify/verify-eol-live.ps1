# 端到端确认：真实 Release（线上 v0.5.0）能不能被判定为安全更新并推到用户面前。
# 不带任何测试假信号——走 UpdateChecker 的真实回退链。有界：最多 150 秒，只关自己启动的 PID。
param([int]$TimeoutSec = 150)
$ErrorActionPreference = 'Stop'
$exe = 'E:\dsh-launcher\src\DshShell\bin\Release\net10.0-windows\DshWeb.exe'
$log = Join-Path $env:USERPROFILE '.dsh\dsh-launcher\dsh.log'
if (-not (Test-Path $exe)) { throw "找不到构建：$exe" }
$existing = @(Get-Process DshWeb -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) { throw "已有实例在跑（PID $($existing.Id -join ',')）" }
Remove-Item Env:\DSH_TEST_UPDATE_SIGNAL -ErrorAction SilentlyContinue

$mark = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
$p = Start-Process -FilePath $exe -PassThru
Write-Host "started pid=$($p.Id) logMark=$mark"
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$seen = @()
try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (-not (Test-Path $log)) { continue }
        $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
        $tail = @()
        try {
            $sr = New-Object System.IO.StreamReader($fs)
            $sr.BaseStream.Seek($mark, 'Begin') | Out-Null
            while (-not $sr.EndOfStream) { $tail += $sr.ReadLine() }
            $sr.Dispose()
        } finally { $fs.Dispose() }
        $seen = @($tail | Where-Object { $_ -match 'update|notice|launcher' })
        if (@($seen | Where-Object { $_ -match 'update notice presented|not presented|suppressed|launcher security' }).Count -gt 0) { break }
    }
    Start-Sleep -Seconds 3
} finally {
    if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 5 }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    Write-Host "closed pid=$($p.Id)"
}
$seen | ForEach-Object { Write-Host "L> $_" }
$utf = @($seen | ForEach-Object { try { ($_ | ConvertFrom-Json).msg } catch { $_ } })
Set-Content -Path 'E:\dsh-launcher\sandbox\issue-verify\eol-live.txt' -Value $utf -Encoding utf8
