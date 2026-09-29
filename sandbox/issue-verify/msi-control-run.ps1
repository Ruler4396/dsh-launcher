# 真机端到端：让安装包装作"这台机器缺 .NET 和 Node"，看它自己走到哪一步。
# 本轮是【对照组】：什么都不点，等旧检查器的 60 秒超时自己触发。
# 只记录，不点击；结束后清掉临时环境变量。
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [int]$WatchSeconds = 150,
    [string]$Tag = 'control'
)
$ErrorActionPreference = 'Stop'
$log = "E:\dsh-launcher\sandbox\issue-verify\msi-$Tag.txt"
if (-not ('QMW' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class QMW {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
}
"@
}
function WinText($h) { $s = New-Object System.Text.StringBuilder 4096; [void][QMW]::GetWindowText($h, $s, $s.Capacity); return $s.ToString() }
function Snapshot() {
    $found = New-Object System.Collections.ArrayList
    $cb = [QMW+EnumProc] { param($h, $l)
        if ([QMW]::IsWindowVisible($h)) {
            $t = WinText $h
            if ($t) { $pp = 0; [void][QMW]::GetWindowThreadProcessId($h, [ref]$pp); [void]$found.Add("$pp|$t") } }
        return $true }
    [void][QMW]::EnumWindows($cb, [IntPtr]::Zero)
    return $found
}

# 用 User 作用域写死，确保经过 UAC 提权后 msiexec 仍能看到（进程内 $env: 不会跨提权传递）
[Environment]::SetEnvironmentVariable('PREREQ_SIMULATE_MISSING', '1', 'User')
$seen = New-Object System.Collections.Generic.HashSet[string]
$lines = New-Object System.Collections.Generic.List[string]
$deadline = (Get-Date).AddSeconds($WatchSeconds)
try {
    $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$Msi`"") -PassThru
    $lines.Add("launched msiexec pid=$($p.Id) msi=$Msi at $(Get-Date -Format HH:mm:ss)")
    $lines.Add('（本轮约定：什么都不点。若弹出 UAC，只有 UAC 需要同意）')
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        foreach ($w in (Snapshot)) {
            if ($w -match 'msiexec|Installer|安装|dsh-launcher|Program Files') {
                if ($seen.Add($w)) {
                    $lines.Add("$(Get-Date -Format HH:mm:ss) 窗口出现: $w")
                }
            }
        }
        if ($p.HasExited) { $lines.Add("$(Get-Date -Format HH:mm:ss) msiexec 已退出 exit=$($p.ExitCode)"); break }
    }
} finally {
    [Environment]::SetEnvironmentVariable('PREREQ_SIMULATE_MISSING', $null, 'User')
    $lines.Add("cleared PREREQ_SIMULATE_MISSING (User scope) at $(Get-Date -Format HH:mm:ss)")
    # 兜底：安装进程还活着就结束它（只杀我们刚启动的那个 PID 及其 msiexec 子进程）
    if (-not $p.HasExited) {
        $lines.Add('收尾：仍有安装进程在跑，按记录 PID 结束')
        Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" | ForEach-Object {
            $lines.Add("  msiexec pid=$($_.ProcessId) cmd=$($_.CommandLine)")
        }
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Set-Content -Path $log -Value $lines -Encoding utf8
    Write-Host "记录写入 $log"
    $lines | ForEach-Object { Write-Host $_ }
}
