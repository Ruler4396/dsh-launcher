# 真机确认：安全更新卡片正文是否真的带上末版公告。
# 有界设计：最多等 120 秒；只杀本脚本亲自记录的那个 PID；绝不按镜像名扫杀。
$ErrorActionPreference = 'Stop'
$exe = 'E:\dsh-launcher\src\DshShell\bin\Release\net10.0-windows\DshWeb.exe'
$log = Join-Path $env:USERPROFILE '.dsh\dsh-launcher\dsh.log'
$deadline = (Get-Date).AddSeconds(120)

if (-not (Test-Path $exe)) { throw "找不到待验构建：$exe" }
$existing = @(Get-Process DshWeb -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) { throw "已有 DshWeb 实例在跑（PID $($existing.Id -join ',')），先确认再跑" }

$mark = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
$env:DSH_TEST_UPDATE_SIGNAL = 'launcher:0.5.0'   # 只替换"远端版本"这一个输入，裁决仍走同源门
$p = Start-Process -FilePath $exe -PassThru
Write-Host "started pid=$($p.Id) logMark=$mark"

$found = @()
try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if (Test-Path $log) {
            $tail = @()
            $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
            try {
                $sr = New-Object System.IO.StreamReader($fs)
                $sr.BaseStream.Seek($mark, 'Begin') | Out-Null
                while (-not $sr.EndOfStream) { $tail += $sr.ReadLine() }
                $sr.Dispose()
            } finally { $fs.Dispose() }
            $hits = @($tail | Where-Object { $_ -match 'update notice presented|not presented|notice suppressed' })
            if ($hits.Count -gt 0) { $found = $hits; $allUpdateLines = @($tail | Where-Object { $_ -match 'update' }); break }
        }
        if ($p.HasExited) { throw "实例提前退出，exit=$($p.ExitCode)" }
    }
} finally {
    if (-not $p.HasExited) {
        $p.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 5
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    }
    Write-Host "instance closed (pid=$($p.Id), exited=$($p.HasExited))"
}

if ($found.Count -eq 0) { Write-Host "TIMEOUT：120 秒内没有呈现痕迹" }
else { $found | ForEach-Object { Write-Host "HIT> $_" } }
if ($allUpdateLines) { $allUpdateLines | ForEach-Object { Write-Host "UPD> $_" } }
