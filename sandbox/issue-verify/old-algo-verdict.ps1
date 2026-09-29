$ErrorActionPreference = 'Stop'
# 用旧算法（只扫进程/持久 PATH + 注册表 InstallPath 的 File.Exists）跑一遍本机，
# 目的：把"改之前这台机器会被判成没有 node"从推导变成实测。
$paths = @([Environment]::GetEnvironmentVariable('PATH', 'Machine'),
           [Environment]::GetEnvironmentVariable('PATH', 'User')) -join ';'
$hitPath = $null
foreach ($d in ($paths -split ';')) {
    if (-not $d) { continue }
    try { $e = Join-Path $d.Trim('"') 'node.exe'; if (Test-Path $e) { $hitPath = $e; break } } catch { }
}
Write-Host "OLD-algorithm PATH hit: $(if ($hitPath) { $hitPath } else { 'NONE' })"
$ip = (Get-ItemProperty 'HKLM:\SOFTWARE\Node.js' -ErrorAction SilentlyContinue).InstallPath
$regExists = if ($ip) { Test-Path (Join-Path $ip 'node.exe') } else { $false }
$regHit = [bool]$ip -and $regExists
Write-Host "OLD-algorithm registry hit: $regHit  (InstallPath=$ip, node.exe exists=$regExists)"
Write-Host "OLD verdict: hasNode=$([bool]($hitPath -or $regHit))"
$fnm = Join-Path $env:APPDATA 'fnm\aliases\default\node.exe'
Write-Host "REALITY: fnm node exists=$(Test-Path $fnm) version=$((& $fnm --version))"
