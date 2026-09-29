param([string]$Tag = 'before', [switch]$Stripped)
$ErrorActionPreference = 'Stop'
$cmd = 'E:\dsh-launcher\scripts\check-prereq.cmd'
$out = "E:\dsh-launcher\sandbox\issue-verify\prereq-cmd-$Tag.txt"
# 用持久（注册表）PATH 跑，剥掉本 shell 被 fnm env 注入的 multishell 目录——
# 双击 .cmd 时 Explorer 给子进程的就是这份 PATH。
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'cmd.exe'
$psi.Arguments = "/c `"$cmd`""
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables['PATH'] =
    [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
    [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($Stripped) {
    # 反例：这台机器上"真的没有 node"该长什么样——所有版本管理器的落点指向空目录
    $empty = 'E:\dsh-launcher\sandbox\issue-verify\prereq-empty'
    New-Item -ItemType Directory -Force -Path $empty | Out-Null
    $psi.EnvironmentVariables['PATH'] = 'C:\Windows\System32'
    foreach ($k in 'FNM_DIR', 'APPDATA', 'LOCALAPPDATA', 'USERPROFILE', 'NVM_SYMLINK', 'VOLTA_HOME') {
        $psi.EnvironmentVariables[$k] = $empty
    }
}
$p = [System.Diagnostics.Process]::Start($psi)
$so = $p.StandardOutput.ReadToEnd(); $se = $p.StandardError.ReadToEnd()
$p.WaitForExit()
Set-Content -Path $out -Value ("exit=" + $p.ExitCode + "`r`n" + $so + $se) -Encoding utf8
Write-Host "$Tag exit=$($p.ExitCode) -> $out"
