# 真机验证新前置检查对话框：文案 + 两个出口各自的退出码。
# 绝不点【是】（那会在本机跑 winget 真装 .NET/Node）；只测【否】=继续安装 与【取消】=1602。
param([string]$Click = 'no')   # no | cancel
$ErrorActionPreference = 'Stop'
$exe = 'E:\dsh-launcher\installer\PrereqCheck\bin\Release\net10.0-windows\win-x64\PrereqCheck.exe'
if (-not (Test-Path $exe)) { throw "没有构建：$exe" }
if (-not ('QPC' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class QPC {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
}
function Text($h) { $s = New-Object System.Text.StringBuilder 4096; [void][QPC]::GetWindowText($h, $s, $s.Capacity); return $s.ToString() }
$env:PREREQ_SIMULATE_MISSING = '1'
$p = Start-Process -FilePath $exe -PassThru
Write-Host "started pid=$($p.Id) click=$Click"
$deadline = (Get-Date).AddSeconds(60)
$dlg = [IntPtr]::Zero
try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $found = New-Object System.Collections.ArrayList
        $cb = [QPC+EnumProc] { param($h, $l)
            if ([QPC]::IsWindowVisible($h)) {
                $pp = 0; [void][QPC]::GetWindowThreadProcessId($h, [ref]$pp)
                if ($pp -eq $p.Id) { [void]$found.Add($h) } }
            return $true }
        [void][QPC]::EnumWindows($cb, [IntPtr]::Zero)
        if ($found.Count -gt 0) { $dlg = $found[0]; break }
    }
    if ($dlg -eq [IntPtr]::Zero) { Write-Host 'TIMEOUT：60 秒内没有对话框（说明检查逻辑没执行到弹窗）' }
    else {
        $t = Text $dlg
        Write-Host "DIALOG> $t"
        Set-Content -Path 'E:\dsh-launcher\sandbox\issue-verify\prereq-dialog.txt' -Value $t -Encoding utf8
        $btnId = if ($Click -eq 'cancel') { 2 } else { 7 }   # 2=IDCANCEL 7=IDNO
        $btn = [QPC]::GetDlgItem($dlg, $btnId)
        if ($btn -eq [IntPtr]::Zero) { Write-Host "找不到按钮 id=$btnId" }
        else { [void][QPC]::PostMessage($btn, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) } # BM_CLICK
        $p.WaitForExit(30000) | Out-Null
        Write-Host "exit=$($p.ExitCode)  (期望：no→0 继续安装 / cancel→1602 用户取消)"
    }
} finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Write-Host 'force-closed' }
    Remove-Item Env:\PREREQ_SIMULATE_MISSING -ErrorAction SilentlyContinue
}
