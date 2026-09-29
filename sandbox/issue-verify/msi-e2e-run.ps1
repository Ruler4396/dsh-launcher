# 真机端到端第二轮：让 MSI 跑真实的前置检查 CA，并按约定点一个按钮，记录 MSI 的反应。
# 只有 UAC 需要人同意；对话框由本脚本点。
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [ValidateSet('none', 'cancel', 'no', 'yes')][string]$Click = 'cancel',
    [int]$WatchSeconds = 240,
    [Parameter(Mandatory = $true)][string]$Tag
)
$ErrorActionPreference = 'Stop'
# msiexec 对 `E:/dir/file.msi` 这种正斜杠路径会直接报 1620「无法打开此安装程序包」
# （第一轮就是这么废的）。命令行侧又必须用正斜杠（反斜杠会被 bash 吞掉），所以在这里统一换算。
$Msi = (Resolve-Path -LiteralPath ($Msi -replace '/', '\')).ProviderPath
$log = "E:\dsh-launcher\sandbox\issue-verify\msi-$Tag.txt"
if (-not ('QMW2' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class QMW2 {
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
function WinText($h) { $s = New-Object System.Text.StringBuilder 4096; [void][QMW2]::GetWindowText($h, $s, $s.Capacity); return $s.ToString() }
function TopWindows {
    $found = New-Object System.Collections.ArrayList
    $cb = [QMW2+EnumProc] { param($h, $l)
        if ([QMW2]::IsWindowVisible($h)) { $t = WinText $h; if ($t) { [void]$found.Add($h) } }
        return $true }
    [void][QMW2]::EnumWindows($cb, [IntPtr]::Zero)
    return $found
}
function ChildTexts($h) {
    $texts = New-Object System.Collections.ArrayList
    $cb = [QMW2+EnumProc] { param($c, $l) $t = WinText $c; if ($t) { [void]$texts.Add($t) }; return $true }
    [void][QMW2]::EnumChildWindows($h, $cb, [IntPtr]::Zero)
    return $texts
}

[Environment]::SetEnvironmentVariable('PREREQ_SIMULATE_MISSING', '1', 'User')
$lines = New-Object System.Collections.Generic.List[string]
$seen = New-Object System.Collections.Generic.HashSet[string]
$deadline = (Get-Date).AddSeconds($WatchSeconds)
$clicked = $false
try {
    $p = Start-Process msiexec.exe -ArgumentList @('/i', $Msi) -PassThru
    $lines.Add("launched pid=$($p.Id) msi=$Msi click=$Click at $(Get-Date -Format HH:mm:ss)")
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        foreach ($h in (TopWindows)) {
            $t = WinText $h
            $key = "$t"
            if (-not $seen.Add($key)) { continue }
            $kids = @(ChildTexts $h)
            $lines.Add("$(Get-Date -Format HH:mm:ss) 窗口: [$t]")
            foreach ($k in $kids) { if ($k -and $k -ne $t) { $lines.Add("    子控件: $k") } }
            if ((-not $clicked) -and ($t -like '*运行环境检查*')) {
                $clicked = $true
                $id = switch ($Click) { 'cancel' { 2 } 'no' { 7 } 'yes' { 6 } default { 0 } }
                if ($id -ne 0) {
                    $btn = [QMW2]::GetDlgItem($h, $id)
                    if ($btn -ne [IntPtr]::Zero) {
                        [void][QMW2]::PostMessage($btn, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
                        $lines.Add("    -> 已点击按钮 id=$id ($(Get-Date -Format HH:mm:ss))")
                    } else { $lines.Add('    -> 找不到目标按钮') }
                } else { $lines.Add('    -> 本轮不点击（观察超时行为）') }
            }
        }
        if ($p.HasExited) {
            $lines.Add("$(Get-Date -Format HH:mm:ss) msiexec 退出 exit=$($p.ExitCode)")
            Start-Sleep -Seconds 3
            break
        }
    }
} finally {
    [Environment]::SetEnvironmentVariable('PREREQ_SIMULATE_MISSING', $null, 'User')
    $lines.Add("cleared env at $(Get-Date -Format HH:mm:ss)")
    if (-not $p.HasExited) {
        $lines.Add('收尾：结束我们启动的 msiexec PID')
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Set-Content -Path $log -Value $lines -Encoding utf8
    Write-Host "记录写入 $log"
}
