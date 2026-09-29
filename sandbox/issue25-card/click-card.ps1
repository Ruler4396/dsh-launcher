param([Parameter(Mandatory)][string]$Exe, [string]$SceneRoot = "$PSScriptRoot")
# 演示"点卡片 = 原来点通知"的动作通道：安全模式提示带退出动作，脚本真的把鼠标点到卡片上，
# 然后从 dsh.log 验证 ExitSafeModeRequested 确实被调用（不是只看它画出来了）。
$ErrorActionPreference = 'Stop'
$scene = Resolve-Path $SceneRoot
$home_ = Join-Path $scene 'home-click'; $wv2 = Join-Path $scene 'wv2-click'
Remove-Item $home_, $wv2 -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $home_, $wv2, (Join-Path $home_ 'dsh-launcher') | Out-Null
Set-Content -Path (Join-Path $home_ 'dsh-launcher\safe-mode.json') -Value '{"active":true,"tier":1}' -Encoding utf8

$port = 39400 + (Get-Random -Maximum 90)
$job = Start-Job -ScriptBlock {
    param($p)
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $p); $l.Start()
    $html = [Text.Encoding]::UTF8.GetBytes('<!doctype html><html><body>click demo</body></html>')
    while ($true) {
        try {
            $c = $l.AcceptTcpClient(); $s = $c.GetStream(); $b = New-Object byte[] 4096
            [void]$s.Read($b, 0, $b.Length)
            $h = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($html.Length)`r`nConnection: close`r`n`r`n"
            $s.Write([Text.Encoding]::ASCII.GetBytes($h)); $s.Write($html); $c.Close()
        } catch { Start-Sleep -m 80 }
    }
} -ArgumentList $port

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $Exe; $psi.WorkingDirectory = Split-Path $Exe; $psi.UseShellExecute = $false
foreach ($k in 'DSH_WEB_URL','DSH_HOME','DSH_SHELL','DSH_SESSION_ID','DSH_SESSION_JSONL','DSH_TEST_UPDATE_SIGNAL') {
    $psi.EnvironmentVariables.Remove($k) | Out-Null }
$psi.EnvironmentVariables['DSH_SANDBOX'] = '1'
$psi.EnvironmentVariables['DSH_HOME'] = $home_
$psi.EnvironmentVariables['DSH_WEB_URL'] = "http://127.0.0.1:$port"
$psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = $wv2
$psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
$psi.EnvironmentVariables['DSH_TEST_INSTALL_MODE'] = 'msi'
$psi.EnvironmentVariables['DSH_TELEMETRY_DISABLED'] = '1'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public struct R { public int L, T, Rt, B; }
}
"@

function Shot([string]$name) {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
    $p = Join-Path $scene "staging\$name.png"; $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); return $p
}

function Find-Card([int]$targetPid) {
    $found = New-Object System.Collections.ArrayList
    $cb = [W+EnumProc] { param($h, $l)
        if ([W]::IsWindowVisible($h)) {
            $p = 0; [void][W]::GetWindowThreadProcessId($h, [ref]$p)
            if ($p -eq $targetPid) {
                $r = New-Object W+R
                if ([W]::GetWindowRect($h, [ref]$r)) {
                    $w = $r.Rt - $r.L; $ht = $r.B - $r.T
                    # 卡片：小窗、位于屏幕右下（主窗是最大化/大窗，被排除）
                    if ($w -gt 200 -and $w -lt 900 -and $ht -gt 60 -and $ht -lt 400) {
                        [void]$found.Add([pscustomobject]@{ H = $h; L = $r.L; T = $r.T; W = $w; Ht = $ht })
                    }
                }
            }
        }
        return $true
    }
    [void][W]::EnumWindows($cb, [IntPtr]::Zero)
    return $found
}

$p = [System.Diagnostics.Process]::Start($psi)
Write-Host "pid=$($p.Id) port=$port"
Start-Sleep -Seconds 10
$card = @(Find-Card $p.Id) | Select-Object -First 1
if (-not $card) { Write-Host "!! 没找到卡片窗口"; }
else {
    Write-Host ("卡片窗口: left={0} top={1} {2}x{3}" -f $card.L, $card.T, $card.W, $card.Ht)
    $before = Shot "click-before"
    Write-Host "点击前截图 -> $before"
    $cx = $card.L + [int]($card.W / 2); $cy = $card.T + [int]($card.Ht / 2)
    [void][W]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 300
    [W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
    [W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
    Write-Host "已在卡片中心点击: ($cx,$cy)"
    Start-Sleep -Seconds 4
    $after = Shot "click-after"
    Write-Host "点击后截图 -> $after"
    $still = @(Find-Card $p.Id)
    Write-Host ("点击后卡片窗口数: {0}（应为 0，即点完即收起）" -f $still.Count)
}
$log = Join-Path $home_ 'dsh-launcher\dsh.log'
Write-Host "`n---- 日志：通知与退出动作 ----"
if (Test-Path $log) {
    Get-Content $log | Where-Object { $_ -match 'notice|SAFEMODE|safe-mode|exited safe|restart' } |
        ForEach-Object { Write-Host $_ }
}
try { $p.Kill($true) } catch {}
Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force
