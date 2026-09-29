# 真机实测：真实 DshWeb.exe（Release）→ 真实鼠标点击标题栏版本徽标 → 弹窗开关 → 进程存活
# 隔离：独立 DSH_HOME / WebView2 数据；--ui-probe 不拉起 dsh 服务，绝不触碰宿主 3080。
# 进度写 sandbox\realcheck-issue28.log（同时 Write-Host），调用方只需读日志/截图。
# 用法：pwsh -File sandbox\realcheck-issue28.ps1
$ErrorActionPreference = "Stop"
$root = "E:\dsh-launcher"
$exe = Join-Path $root "src\DshShell\bin\Release\net10.0-windows\DshWeb.exe"
$work = Join-Path $root "sandbox\realrun-issue28"
$dshHome = Join-Path $work "home"
$wv2 = Join-Path $work "wv2"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dshHome, $wv2 | Out-Null

$log = Join-Path $root "sandbox\realcheck-issue28.log"
function Say([string]$m) {
    $line = "{0} {1}" -f (Get-Date -Format "HH:mm:ss"), $m
    Write-Host $line
    Add-Content -Path $log -Value $line -Encoding utf8
}
Set-Content -Path $log -Value "=== realcheck start $(Get-Date -Format s) ===" -Encoding utf8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr FindByTitle(string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      var sb = new StringBuilder(512);
      GetWindowTextW(h, sb, sb.Capacity);
      if (sb.ToString() == title) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

function Shot([int]$x, [int]$y, [int]$w, [int]$h, [string]$path) {
    $b = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose(); $b.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
}

$psi = New-Object System.Diagnostics.ProcessStartInfo($exe, "--ui-probe")
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path $exe
$psi.Environment["DSH_HOME"] = $dshHome
$psi.Environment["DSH_TEST_MODE"] = "1"
$psi.Environment["DSH_VERSION"] = "0.1.5-rc.1"          # 强制出徽标（隔离 home 下发现链版本未知）
$psi.Environment["DSH_WEBVIEW2_DATA"] = $wv2
# 关键：重定向子进程输出（否则子进程及其 WebView2 子孙继承本脚本 stdout 句柄，调用方管道永不关闭）
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
[void]$proc.Start()
$proc.BeginOutputReadLine()
$proc.BeginErrorReadLine()
Say "probe pid=$($proc.Id) exe=$exe"

$hwnd = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline -and $hwnd -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 300
    $hwnd = [W]::FindByTitle("DeepSeek Harness")
}
if ($hwnd -eq [IntPtr]::Zero) {
    Say "FAIL: 主窗 40s 未出现"
    if (-not $proc.HasExited) { $proc.Kill($true) }
    Say "RESULT: FAIL"; exit 1
}
Start-Sleep -Milliseconds 1500
[void][W]::SetForegroundWindow($hwnd)

$r = New-Object W+RECT
[void][W]::GetWindowRect($hwnd, [ref]$r)
$g = [System.Drawing.Graphics]::FromHwnd($hwnd); $dpi = $g.DpiX; $g.Dispose()
$scale = $dpi / 96.0
$font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)
$titleW = [System.Windows.Forms.TextRenderer]::MeasureText("DeepSeek Harness", $font).Width
$badge = [DshWeb.ShellLogic]::VersionInfoPolicy.ComposeTitleBarBadge("0.1.5-rc.1")
$badgeW = [System.Windows.Forms.TextRenderer]::MeasureText($badge, $font).Width
$titleLeft = [int][Math]::Round(34 * $scale); $gap = [int][Math]::Round(4 * $scale)
$badgeX = $titleLeft + $titleW + $gap
$clickX = $r.Left + 1 + $badgeX + [int]($badgeW / 2)
$clickY = $r.Top + 1 + [int](16 * $scale)
Say "badge='$badge' rect=$($r.Left),$($r.Top)-$($r.Right),$($r.Bottom) dpi=$dpi scale=$scale"
Say "click at ($clickX,$clickY) [titleW=$titleW badgeX=$badgeX badgeW=$badgeW]"
Shot $r.Left $r.Top 700 ([int](40 * $scale)) (Join-Path $work "titlebar-before-click.png")

[void][W]::SetCursorPos($clickX - 150, $clickY)   # 先移入标题栏再进徽标（触发 MouseMove 命中矩形）
Start-Sleep -Milliseconds 200
[void][W]::SetCursorPos($clickX, $clickY)
Start-Sleep -Milliseconds 300
[W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
Start-Sleep -Milliseconds 90
[W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP

$dlg = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline -and $dlg -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 200
    $dlg = [W]::FindByTitle("版本信息")
}
if ($dlg -eq [IntPtr]::Zero) {
    Say "FAIL: 真实点击后版本信息窗未出现（进程存活=$(-not $proc.HasExited)）"
    if (-not $proc.HasExited) { $proc.Kill($true) }
    Say "RESULT: FAIL"; exit 1
}
Say "OK: 版本信息窗出现 hwnd=0x$($dlg.ToInt64().ToString('X'))，进程存活=$(-not $proc.HasExited)"
$dr = New-Object W+RECT; [void][W]::GetWindowRect($dlg, [ref]$dr)
Shot $dr.Left $dr.Top ($dr.Right - $dr.Left) ($dr.Bottom - $dr.Top) (Join-Path $work "version-dialog.png")

[void][W]::PostMessageW($dlg, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE（等价点 X / ESC）
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline -and [W]::FindByTitle("版本信息") -ne [IntPtr]::Zero) { Start-Sleep -Milliseconds 200 }
$closed = [W]::FindByTitle("版本信息") -eq [IntPtr]::Zero
Start-Sleep -Milliseconds 600
Shot $r.Left $r.Top 700 ([int](40 * $scale)) (Join-Path $work "titlebar-after-close.png")
Say "弹窗已关闭=$closed 进程存活=$(-not $proc.HasExited) 退出码=$(if($proc.HasExited){$proc.ExitCode}else{'n/a'})"

for ($i = 2; $i -le 3; $i++) {
    [void][W]::SetCursorPos($clickX, $clickY); Start-Sleep -Milliseconds 200
    [W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 80
    [W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    $d2 = [IntPtr]::Zero; $dl2 = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $dl2 -and $d2 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 150; $d2 = [W]::FindByTitle("版本信息") }
    if ($d2 -eq [IntPtr]::Zero) { Say "round $i : FAIL 弹窗未出现"; break }
    [void][W]::PostMessageW($d2, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); Start-Sleep -Milliseconds 600
    Say "round $i : 弹窗开关正常，进程存活=$(-not $proc.HasExited)"
}

$ok = $closed -and (-not $proc.HasExited)
if (-not $proc.HasExited) { $proc.Kill($true) }
if ($ok) { Say "RESULT: PASS（真机真实点击全链路通过，未闪退）" } else { Say "RESULT: FAIL" }
