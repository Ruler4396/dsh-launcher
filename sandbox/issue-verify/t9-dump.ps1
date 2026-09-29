param([int]$StartBudget = 170)
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'lib.ps1')
# Diagnostic pass for T9: start the tray scene, close the window to the tray, then dump the
# notification-area UIA tree so the icon can be located (and right-clicked) for real.
$c = Q-NewScene -Name 'T9'
$marker = Join-Path $c.Home 'profiles\web\node_modules\dsh-launcher-lifetime'
New-Item -ItemType Directory -Force -Path $marker | Out-Null
Set-Content -Path (Join-Path $c.Home 'dsh-launcher\settings.json') -Value '{"serviceLifetime":1}' -Encoding utf8
Q-Start $c | Out-Null
try {
    $r = Q-WaitLog $c @('HEALTHY', 'service readiness failed') $StartBudget
    Write-Host "  service: $($r.hit)"
    $w = Q-WaitWindow $c 'DeepSeek Harness*' 40
    $cx = $w.X + $w.W - 24; $cy = $w.Y + 16
    Q-Click $cx $cy 'close (X) #1'
    Start-Sleep -Seconds 2
    if (@(Q-FindWindows $c 'DeepSeek Harness*').Count -gt 0) { Q-Click $cx $cy 'close (X) #2' }
    Start-Sleep -Seconds 3
    Write-Host ("  windows after close = {0}; host alive = {1}" -f @(Q-FindWindows $c 'DeepSeek Harness*').Count, (-not $c.Proc.HasExited))
    & (Join-Path $VerifyRoot 'tray-probe.ps1') -NameHint 'dsh' -Dump
    Write-Host "  probe rc=$LASTEXITCODE"
} finally { Q-Stop $c }
