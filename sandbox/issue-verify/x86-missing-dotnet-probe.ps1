# 用 32 位检查器做"真缺 .NET"探针：x86 进程的 ProgramFiles 指向 C:\Program Files (x86)，
# 那里确实没有 dotnet\shared\Microsoft.WindowsDesktop.App —— 于是 .NET 缺失是**真实检测出来的**，
# 不是 PREREQ_SIMULATE_MISSING 模拟的。脚本记录对话框文字并点【取消】，看退出码。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [ValidateSet('cancel', 'no', 'none')][string]$Click = 'cancel',
    [int]$TimeoutSec = 90
)
$ErrorActionPreference = 'Stop'
Remove-Item Env:\PREREQ_SIMULATE_MISSING -ErrorAction SilentlyContinue
if (-not ('QP' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class QP {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
}
function T($h) { $s = New-Object System.Text.StringBuilder 8192; [void][QP]::GetWindowText($h, $s, $s.Capacity); return $s.ToString() }
$p = Start-Process -FilePath $Exe -PassThru
Write-Host "started pid=$($p.Id) exe=$Exe"
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$dlg = [IntPtr]::Zero
try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $found = New-Object System.Collections.ArrayList
        $cb = [QP+EnumProc] { param($h, $l)
            if ([QP]::IsWindowVisible($h)) {
                $pp = 0; [void][QP]::GetWindowThreadProcessId($h, [ref]$pp)
                if ($pp -eq $p.Id) { [void]$found.Add($h) } }
            return $true }
        [void][QP]::EnumWindows($cb, [IntPtr]::Zero)
        if ($found.Count -gt 0) { $dlg = $found[0]; break }
    }
    if ($dlg -eq [IntPtr]::Zero) { Write-Host 'TIMEOUT：没有对话框（说明这台机器被判定为环境齐全）' }
    else {
        $texts = New-Object System.Collections.ArrayList
        $kcb = [QP+EnumProc] { param($c, $l) $t = T $c; if ($t) { [void]$texts.Add($t) }; return $true }
        [void][QP]::EnumChildWindows($dlg, $kcb, [IntPtr]::Zero)
        $body = T $dlg
        Write-Host "DIALOG> $body"
        $texts | ForEach-Object { Write-Host "  控件> $_" }
        Set-Content -Path 'E:\dsh-launcher\sandbox\issue-verify\x86-probe-dialog.txt' `
            -Value (@("TITLE> $body") + @($texts)) -Encoding utf8
        if ($Click -ne 'none') {
            $id = if ($Click -eq 'cancel') { 2 } else { 7 }
            $btn = [QP]::GetDlgItem($dlg, $id)
            if ($btn -ne [IntPtr]::Zero) { [void][QP]::PostMessage($btn, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
            $p.WaitForExit(20000) | Out-Null
            Write-Host "exit=$($p.ExitCode)  (期望 1602)"
        }
    }
} finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Write-Host 'force-closed' }
}
