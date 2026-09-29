param([string]$NameHint = 'DeepSeek', [switch]$RightClick, [switch]$OverflowFirst, [switch]$Dump,
      [switch]$Sweep, [int]$TargetPid = 0, [int]$MenuW = 0, [int]$MenuH = 0)
# Locate the shell's tray icon through UI Automation and (optionally) right-click it.
# The notification area is explorer-owned; icons may live in the overflow flyout, so the
# chevron has to be opened first. Everything here is best-effort with printed evidence -
# if the icon cannot be found the caller reports SKIP rather than pretending.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Tray {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  public static bool MenuVisible(int pid, int w, int h) {
    bool found = false;
    EnumProc cb = delegate (IntPtr hwnd, IntPtr l) {
      if (!IsWindowVisible(hwnd)) return true;
      int p; GetWindowThreadProcessId(hwnd, out p);
      if (p != pid) return true;
      R r; GetWindowRect(hwnd, out r);
      if ((r.Rt - r.L) == w && (r.B - r.T) == h) { found = true; return false; }
      return true; };
    EnumWindows(cb, IntPtr.Zero);
    return found; }
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  public static void Dismiss() { keybd_event(0x1B, 0, 0, IntPtr.Zero); keybd_event(0x1B, 0, 2, IntPtr.Zero); }
  public static void RightClick(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(250);
    mouse_event(0x0008, 0, 0, 0, IntPtr.Zero); mouse_event(0x0010, 0, 0, 0, IntPtr.Zero); }
  public static void LeftClick(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(250);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }
}
"@
$auto = [System.Windows.Automation.AutomationElement]
$root = $auto::RootElement
$script:Toolbars = @()
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.Condition]::TrueCondition)
Write-Host ("top-level windows: {0}" -f $all.Count)
$tray = $null; $overflow = $null
foreach ($w in $all) {
    $ct = $w.Current.ClassName
    if ($ct -eq 'Shell_TrayWnd') { $tray = $w }
    if ($ct -eq 'NotifyIconOverflowWindow') { $overflow = $w }
}
Write-Host ("Shell_TrayWnd={0} NotifyIconOverflowWindow={1}" -f ($null -ne $tray), ($null -ne $overflow))
function Dump-Items($scope, [string]$label) {
    if (-not $scope) { return @() }
    $found = @()
    $items = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $n = 0
    foreach ($i in $items) {
        $nm = $i.Current.Name; $c = $i.Current.ClassName; $r = $i.Current.BoundingRectangle
        $n++
        if ($Dump -and $n -le 80 -and ($nm -or $Dump)) {
            Write-Host ("  ALL[{0}] name=[{1}] class={2} ctl={3} rect={4},{5} {6}x{7}" -f `
                $label, $nm, $c, $i.Current.ControlType.ProgrammaticName, `
                [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
        }
        # 顺带记下通知区域的 legacy toolbar：Win10 不把图标暴露成 UIA 子元素，
        # 扫描落点时要用这些矩形（第二次 FindAll 会因元素失效抛 COM 异常，所以在这里一次取全）。
        if ($c -eq 'ToolbarWindow32') { $script:Toolbars += ,@([string]$nm, $r) }
        if ($nm -and ($nm -match $NameHint -or $nm -match 'dsh')) {
            Write-Host ("  [{0}] name=[{1}] class={2} rect={3},{4} {5}x{6}" -f `
                $label, $nm, $c, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
            $found += ,@($nm, [int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2))
        }
    }
    Write-Host ("  ({0} descendants under {1})" -f $n, $label)
    return $found
}
if ($OverflowFirst -and $tray) {
    $chev = $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -match '显示隐藏的图标|Show hidden icons|通知' } |
        Select-Object -First 1
    if ($chev) {
        $r = $chev.Current.BoundingRectangle
        Write-Host ("  opening overflow via [{0}] at {1},{2}" -f $chev.Current.Name, [int]($r.X+$r.Width/2), [int]($r.Y+$r.Height/2))
        [Tray]::LeftClick([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2))
        Start-Sleep -Seconds 2
    } else { Write-Host "  no overflow chevron found by name" }
}
$hits = @(Dump-Items $tray 'tray') + @(Dump-Items $overflow 'overflow')
Write-Host ("  toolbars recorded: {0}" -f @($script:Toolbars).Count)
if ($hits.Count -eq 0 -and -not $Sweep) { Write-Host "NO TRAY ICON FOUND for hint [$NameHint]"; exit 3 }
if ($hits.Count -gt 0 -and $RightClick) {
    $h = $hits[0]
    Write-Host ("  right-clicking [{0}] at ({1},{2})" -f $h[0], $h[1], $h[2])
    [Tray]::RightClick($h[1], $h[2])
    Start-Sleep -Seconds 1
}

# Win10 的通知区域把图标放在 legacy ToolbarWindow32 里，UIA 不把它们暴露成子元素
# （实测整棵 Shell_TrayWnd 只有 15 个后代，图标一个都没有）。所以退而求其次：
# 按 24x24 的格子逐个真右键，看目标进程有没有冒出预期尺寸的自绘菜单——
# 这不是"猜坐标点窗口"，而是把通知区域扫一遍并**用被验证方自己的窗口**认领结果。
if ($Sweep -and $TargetPid -gt 0 -and $MenuW -gt 0 -and $MenuH -gt 0) {
    # 图标默认藏在通知区域的溢出 flyout 里（用户说的"默认是不打开的"）。flyout 是 explorer
    # 的另一个顶层窗口 NotifyIconOverflowWindow，只在打开期间存在——所以要先开、再取证。
    $flyAreas = @()
    $areas = @($script:Toolbars)
    $chev = $null
    foreach ($t in $script:Toolbars) { if ($t[0] -match '通知区域|Notification') { $chev = $t[1]; break } }
    for ($try = 0; $try -lt 3; $try++) {
        $fly = $null
        foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children,
                    [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($w.Current.ClassName -eq 'NotifyIconOverflowWindow') { $fly = $w; break }
        }
        if (-not $fly) {
            if ($chev) {
                [Tray]::LeftClick([int]($chev.X + $chev.Width/2), [int]($chev.Y + $chev.Height/2))
                Write-Host "  opened overflow flyout (attempt $($try+1))"
            }
            Start-Sleep -Milliseconds 1200
            continue
        }
        foreach ($i in $fly.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($i.Current.ClassName -eq 'ToolbarWindow32') {
                $r = $i.Current.BoundingRectangle
                Write-Host ("  overflow toolbar name=[{0}] rect={1},{2} {3}x{4}" -f `
                    $i.Current.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
                $areas += ,@([string]$i.Current.Name, $r)
                $flyAreas += ,@([string]$i.Current.Name, $r)
            }
        }
        break
    }
    # 先扫 flyout（我们的图标在里面的概率最高），再扫常驻区——少碰用户自己的图标
    $areas = @($flyAreas) + @($areas)
    Write-Host ("  sweeping {0} toolbar area(s) for pid {1} menu {2}x{3}" -f $areas.Count, $TargetPid, $MenuW, $MenuH)
    foreach ($a in $areas) {
        $r = $a[1]
        $cell = 24
        $cols = [Math]::Max(1, [int]($r.Width / $cell)); $rows = [Math]::Max(1, [int]($r.Height / $cell))
        for ($i = 0; $i -lt $cols; $i++) {
            for ($j = 0; $j -lt $rows; $j++) {
                $x = [int]($r.X + $i * $cell + $cell / 2); $y = [int]($r.Y + $j * $cell + $cell / 2)
                [Tray]::RightClick($x, $y); Start-Sleep -Milliseconds 900
                if ([Tray]::MenuVisible($TargetPid, $MenuW, $MenuH)) {
                    Write-Host ("  SWEEP HIT: menu owned by pid {0} appeared after right-click ({1},{2}) in area [{3}]" -f $TargetPid, $x, $y, $a[0])
                    exit 0
                }
                [Tray]::Dismiss(); Start-Sleep -Milliseconds 250
            }
        }
    }
    Write-Host "  sweep found no menu for the target pid"
    exit 3
}
