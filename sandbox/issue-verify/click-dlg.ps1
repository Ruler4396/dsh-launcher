param([string]$TitleContains = '', [int]$Id = 6, [int]$Pid_ = 0, [switch]$List, [switch]$Once)
# Click a dialog button by its control id (real mouse click at the button's screen rect).
# SetForegroundWindow is unreliable from a script (Windows focus-lock), so keyboard Enter
# can land on the wrong window; GetDlgItem + GetWindowRect + mouse_event is deterministic.
# IDs: 1=OK 2=Cancel 6=Yes 7=No 4=Retry 8=Close.
$ErrorActionPreference = 'Stop'
if (-not ('QDlg' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class QDlg {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public struct R { public int L, T, Rt, B; }
}
"@
}
function Text($h) { $s = New-Object System.Text.StringBuilder 256; [void][QDlg]::GetWindowText($h, $s, $s.Capacity); return $s.ToString() }
$targets = New-Object System.Collections.ArrayList
$cb = [QDlg+EnumProc] { param($h, $l)
    if ([QDlg]::IsWindowVisible($h)) {
        $t = Text $h
        $p = 0; [void][QDlg]::GetWindowThreadProcessId($h, [ref]$p)
        $okTitle = ($TitleContains -eq '') -or ($t -like "*$TitleContains*")
        $okPid = ($Pid_ -eq 0) -or ($p -eq $Pid_)
        if ($okTitle -and $okPid -and $t.Length -gt 0) {
            [void]$targets.Add([pscustomobject]@{ H = $h; Title = $t; Pid = $p }) } }
    return $true }
[void][QDlg]::EnumWindows($cb, [IntPtr]::Zero)
if ($List) { $targets | ForEach-Object { Write-Host ("hwnd={0} pid={1} title=[{2}]" -f $_.H, $_.Pid, $_.Title) }; return }
if ($targets.Count -eq 0) { Write-Host "no matching window"; exit 3 }
$dlg = $targets[0]
$child = [QDlg]::GetDlgItem($dlg.H, $Id)
if ($child -eq [IntPtr]::Zero) {
    Write-Host ("dialog [{0}] has no control id {1}; children:" -f $dlg.Title, $Id)
    $cb2 = [QDlg+EnumProc] { param($h, $l)
        $r = New-Object QDlg+R; [void][QDlg]::GetWindowRect($h, [ref]$r)
        Write-Host ("  child hwnd={0} id={1} text=[{2}] rect={3}x{4}" -f $h, [QDlg]::GetDlgCtrlID($h), (Text $h), ($r.Rt-$r.L), ($r.B-$r.T))
        return $true }
    [void][QDlg]::EnumChildWindows($dlg.H, $cb2, [IntPtr]::Zero)
    exit 4
}
$rr = New-Object QDlg+R
[void][QDlg]::GetWindowRect($child, [ref]$rr)
$x = [int](($rr.L + $rr.Rt) / 2); $y = [int](($rr.T + $rr.B) / 2)

function DlgAlive($h) { return [QDlg]::IsWindowVisible($h) }

# 第一击常常只把非前台窗口"激活"掉（WM_MOUSEACTIVATE 吃掉点击），所以要验证并补击；
# 两次物理点击仍在（焦点被系统抢回、或窗口被别的程序压在下面）就退到 BM_CLICK——
# 它仍走窗口自己的消息通路（等价于用户按下按钮），只是不经物理坐标。
[void][QDlg]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 350
[QDlg]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); [QDlg]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Write-Host ("clicked id={0} at ({1},{2}) in dialog [{3}] pid={4}" -f $Id, $x, $y, $dlg.Title, $dlg.Pid)
Start-Sleep -Milliseconds 700
if (-not $Once -and (DlgAlive $dlg.H)) {
    Write-Host "  dialog still open -> second physical click"
    [void][QDlg]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 350
    [QDlg]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); [QDlg]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}
if (-not $Once -and (DlgAlive $dlg.H)) {
    Write-Host "  still open -> PostMessage BM_CLICK to the button"
    [void][QDlg]::PostMessage($child, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
    Write-Host ("  after BM_CLICK, dialog alive = {0}" -f (DlgAlive $dlg.H))
}
Write-Host ("dialog alive at exit = {0}" -f (DlgAlive $dlg.H))
