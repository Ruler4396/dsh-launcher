# 演示用：在"已更新到 0.1.5-rc.2"的那个沙盒上再启一次，把主窗带到前台截图。
# 复用 scene-T3R/home（runtimes/0.1.5-rc.2 已在里面），所以这一次启动本身就是
# "更新成功重启后"的状态；顺带证明同一信号不再提示更新。
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'lib.ps1')
$c = Q-NewScene -Name 'T3R' -Reuse -ExtraEnv @{ 'DSH_TEST_UPDATE_SIGNAL' = 'dsh:0.1.5-rc.2' }
Q-Start $c | Out-Null
try {
    $r = Q-WaitLogPid $c @('HEALTHY', 'service readiness failed', 'E2002') 240
    Write-Host "  readiness: $($r.hit)"
    $w = $null; $dl = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $dl) {
        $all = @(Q-FindWindows $c '' | Where-Object { $_.W -gt 700 -and $_.Ht -gt 500 } | Sort-Object W -Descending)
        if ($all.Count -gt 0) { $w = $all[0]; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $w) { Write-Host '  no main window'; exit 1 }
    # SetForegroundWindow 从后台进程调用会被 Windows 的前台锁挡掉（实测窗口留在别人下面，
    # 截出来是用户自己的浏览器）。标准解法：先模拟一次 ALT 键解除锁，再确认真的到前台。
    $ok = $false
    for ($i = 1; $i -le 4; $i++) {
        [QWin]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [QWin]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
        [void][QWin]::SetForegroundWindow($w.H); Start-Sleep -Milliseconds 600
        if ([QWin]::GetForegroundWindow() -eq $w.H) { $ok = $true; break }
        Write-Host "  foreground attempt $i failed (fg=$([QWin]::GetForegroundWindow()))"
    }
    Start-Sleep -Seconds 1
    Write-Host ("  window {0}x{1} at ({2},{3}) title=[{4}] foreground={5}" -f $w.W, $w.Ht, $w.X, $w.Y, $w.Title, $ok)
    $p = Q-Shot $c 'T3R-3-after-badge'
    # 截图窗口本体 + 右侧留 40px，供肉眼核对标题栏版本徽标
    pwsh -NoProfile -File (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'crop.ps1') `
        -Src $p -X ($w.X - 6) -Y ($w.Y - 6) -W ($w.W + 12) -H ([Math]::Min($w.Ht + 12, 1000)) `
        -MaxWidth 1100 -Out (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'shots/demo-2-after.png')
    # 取**本次 pid** 的行：日志跨启动共用，正则抓第一条会抓到上一轮旧运行时的拉起命令
    $mine = @((Q-Log $c) -split "`n" | Where-Object { $_ -match "`"pid`":$($c.Proc.Id)," }) -join "`n"
    $id = [regex]::Matches($mine, 'service start via identity[^"]{0,220}')
    Write-Host ("  {0}" -f $id[$id.Count - 1].Value)
    Write-Host ("  notice verdict: " + [regex]::Match($mine, 'update notice suppressed: [a-z-]+').Value)
} finally { Q-Stop $c }
