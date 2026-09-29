# 旁观记录器：不改任何东西，只把与安装有关的窗口按时间线记下来。
param([int]$Minutes = 12)
$ErrorActionPreference = 'Stop'
$out = 'E:\dsh-launcher\sandbox\issue-verify\watch-timeline.txt'
if (-not ('WT' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class WT {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
"@
}
function T($h) { $s = New-Object System.Text.StringBuilder 8192; [void][WT]::GetWindowText($h, $s, $s.Capacity); return $s.ToString() }
$prev = @{}
Set-Content -Path $out -Value "watch started $(Get-Date -Format 'HH:mm:ss')" -Encoding utf8
$deadline = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $deadline) {
    $now = @{}
    $cb = [WT+EnumProc] { param($h, $l)
        if ([WT]::IsWindowVisible($h)) {
            $t = T $h
            if ($t -and ($t -like '*dsh-launcher*' -or $t -like '*Windows Installer*' -or $t -like '*安装*')) {
                $texts = New-Object System.Collections.ArrayList
                $kcb = [WT+EnumProc] { param($c, $l2) $k = T $c; if ($k) { [void]$texts.Add($k) }; return $true }
                [void][WT]::EnumChildWindows($h, $kcb, [IntPtr]::Zero)
                $now["$t"] = ($texts | Select-Object -Unique) -join ' / '
            } }
        return $true }
    [void][WT]::EnumWindows($cb, [IntPtr]::Zero)
    foreach ($k in $now.Keys) {
        if (-not $prev.ContainsKey($k) -or $prev[$k] -ne $now[$k]) {
            Add-Content -Path $out -Value "$(Get-Date -Format 'HH:mm:ss')  [$k]  内容: $($now[$k])" -Encoding utf8
        }
    }
    foreach ($k in @($prev.Keys)) { if (-not $now.ContainsKey($k)) { Add-Content -Path $out -Value "$(Get-Date -Format 'HH:mm:ss')  窗口消失 [$k]" -Encoding utf8 } }
    $prev = $now
    Start-Sleep -Milliseconds 700
}
Add-Content -Path $out -Value "watch ended $(Get-Date -Format 'HH:mm:ss')" -Encoding utf8
