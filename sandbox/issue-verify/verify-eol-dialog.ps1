# 真机确认：便携版"安全更新"决策对话框正文是否带上末版公告。
# 本机这份构建被判定为便携版，launcher 安全更新走 MessageBox（与卡片同一份文案来源）。
# 有界：最多等 90 秒；只操作/只杀本脚本记录的那个 PID；读完点"否"(id 7) 关掉，再关主窗。
param([int]$TimeoutSec = 90, [string]$Signal = 'launcher:0.5.0')
$ErrorActionPreference = 'Stop'
$exe = 'E:\dsh-launcher\src\DshShell\bin\Release\net10.0-windows\DshWeb.exe'
if (-not (Test-Path $exe)) { throw "找不到待验构建：$exe" }
$existing = @(Get-Process DshWeb -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) { throw "已有 DshWeb 实例在跑（PID $($existing.Id -join ',')）" }

if (-not ('QDl2' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class QDl2 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
}
function Text($h) { $s = New-Object System.Text.StringBuilder 4096; [void][QDl2]::GetWindowText($h, $s, $s.Capacity); return $s.ToString() }

if ($Signal) { $env:DSH_TEST_UPDATE_SIGNAL = $Signal }
else { Remove-Item Env:\DSH_TEST_UPDATE_SIGNAL -ErrorAction SilentlyContinue; Write-Host 'no test hook: hitting the live Release' }
$p = Start-Process -FilePath $exe -PassThru
Write-Host "started pid=$($p.Id)"
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$dlg = [IntPtr]::Zero
try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $found = New-Object System.Collections.ArrayList
        $cb = [QDl2+EnumProc] { param($h, $l)
            if ([QDl2]::IsWindowVisible($h)) {
                $t = Text $h
                $pp = 0; [void][QDl2]::GetWindowThreadProcessId($h, [ref]$pp)
                if ($pp -eq $p.Id -and $t -like '*安全更新*') { [void]$found.Add($h) } }
            return $true }
        [void][QDl2]::EnumWindows($cb, [IntPtr]::Zero)
        if ($found.Count -gt 0) { $dlg = $found[0]; break }
    }
    if ($dlg -eq [IntPtr]::Zero) { Write-Host "TIMEOUT：$TimeoutSec 秒内没等到安全更新对话框" }
    else {
        $kids = New-Object System.Collections.ArrayList
        $kcb = [QDl2+EnumProc] { param($h, $l) [void]$kids.Add((Text $h)); return $true }
        [void][QDl2]::EnumChildWindows($dlg, $kcb, [IntPtr]::Zero)
        Write-Host "DIALOG TITLE> $(Text $dlg)"
        foreach ($k in $kids) { if ($k) { Write-Host "CHILD> $k" } }
        # 控制台是 GBK，中文会花屏；同一份文本按 UTF-8 落盘留证
        Set-Content -Path 'E:\dsh-launcher\sandbox\issue-verify\eol-dialog.txt' -Encoding utf8 `
            -Value (@("TITLE> $(Text $dlg)") + ($kids | Where-Object { $_ }))
        $no = [QDl2]::GetDlgItem($dlg, 7)   # 否 = IDNO 7：不打开下载页，只确认文案
        if ($no -ne [IntPtr]::Zero) { [void][QDl2]::PostMessage($no, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) } # BM_CLICK
        Start-Sleep -Seconds 2
    }
} finally {
    if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 5 }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    Write-Host "instance closed (pid=$($p.Id))"
}
