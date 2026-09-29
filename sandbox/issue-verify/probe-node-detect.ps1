$ErrorActionPreference = 'Stop'
function RegPath($hive, $key) {
    try { return [string](Get-ItemProperty -Path "$hive::$key" -Name Path -ErrorAction Stop).Path } catch { return '' }
}
$machine = RegPath 'HKLM' 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
$user = RegPath 'HKCU' 'Environment'
$registryPath = @($machine, $user) -join ';'
Write-Host "=== 注册表 PATH（新进程 / msiexec 实际看到的）==="
foreach ($p in ($registryPath -split ';' | Where-Object { $_ })) { Write-Host "  $p" }
Write-Host "=== 本 shell 的 PATH 里有没有 node.exe（会被 profile 注入污染）==="
Write-Host "  this-shell PATH has node dir: $((($env:PATH -split ';') | Where-Object { Test-Path (Join-Path $_ 'node.exe') -ErrorAction SilentlyContinue }) -join ' | ')"
Write-Host "=== 注册表 PATH 里能不能找到 node.exe ==="
$found = @()
foreach ($p in ($registryPath -split ';')) {
    if (-not $p) { continue }
    try { $exe = Join-Path ($p.Trim('"')) 'node.exe'; if (Test-Path $exe) { $found += $exe } } catch { }
}
if ($found.Count -eq 0) { Write-Host '  NONE —— 从注册表 PATH 找不到任何 node.exe' }
else { $found | ForEach-Object { Write-Host "  $_  ($(& $_ --version))" } }
Write-Host "=== 官方安装器 / fnm / nvm 的落点 ==="
foreach ($probe in @(
        'C:\Program Files\nodejs\node.exe',
        "$env:APPDATA\fnm",
        "$env:LOCALAPPDATA\fnm",
        "$env:USERPROFILE\.fnm",
        "$env:APPDATA\nvm",
        "$env:USERPROFILE\AppData\Roaming\npm")) {
    Write-Host ("  {0} -> {1}" -f $probe, (Test-Path $probe))
}
Write-Host "=== fnm 的 node-versions 实际内容 ==="
$fnmRoots = @("$env:APPDATA\fnm\node-versions", "$env:LOCALAPPDATA\fnm\node-versions", "$env:USERPROFILE\.fnm\node-versions")
foreach ($r in $fnmRoots) {
    if (Test-Path $r) {
        Write-Host "  root=$r"
        Get-ChildItem $r -Directory | ForEach-Object {
            $n = Join-Path $_.FullName 'installation\node.exe'
            Write-Host ("    {0} installation\node.exe -> {1}" -f $_.Name, (Test-Path $n))
        }
        $alias = Join-Path $r '..\aliases'
        if (Test-Path $alias) { Get-ChildItem $alias | ForEach-Object { Write-Host "    alias $($_.Name)" } }
    }
}
Write-Host "=== 注册表 Node.js\InstallPath（MSI 的兜底判据）==="
foreach ($k in 'HKLM:\SOFTWARE\Node.js', 'HKLM:\SOFTWARE\WOW6432Node\Node.js') {
    if (Test-Path $k) { Write- "  $k -> $((Get-ItemProperty $k).InstallPath)" } else { Write-Host "  $k -> 不存在" }
}
Write-Host "=== 现在活着的服务用的是哪个 node ==="
Get-CimInstance Win32_Process -Filter "Name='node.exe'" | ForEach-Object { Write-Host "  pid=$($_.ProcessId) $($_.ExecutablePath)" }
