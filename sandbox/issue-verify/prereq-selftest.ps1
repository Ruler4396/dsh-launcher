$ErrorActionPreference = 'Stop'
$exe = 'E:\dsh-launcher\installer\PrereqCheck\bin\Release\net10.0-windows\win-x64\PrereqCheck.exe'
if (-not (Test-Path $exe)) { throw "no prereqcheck build: $exe" }
$out = 'E:\dsh-launcher\sandbox\issue-verify\prereq-selftest'
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host '=== 正例：本机现状（node 只由 fnm 提供，注册表 PATH 里一个 node.exe 都没有）==='
$p1 = Join-Path $out 'positive.txt'
# PrereqCheck 是 WinExe（GUI 子系统）：`& exe` 不等待也不填 $LASTEXITCODE，必须 Start-Process -Wait
$r1 = Start-Process -FilePath $exe -ArgumentList @('--selftest-node', $p1) -Wait -PassThru
Write-Host "exit=$($r1.ExitCode)"; Get-Content $p1 | ForEach-Object { Write-Host "  $_" }

Write-Host '=== 决定性一例：把进程 PATH 换成注册表里的持久 PATH（模拟 msiexec 看到的世界）==='
$p3 = Join-Path $out 'persistent-path.txt'
$env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
            [Environment]::GetEnvironmentVariable('PATH', 'User')
$r3 = Start-Process -FilePath $exe -ArgumentList @('--selftest-node', $p3) -Wait -PassThru
Write-Host "exit=$($r3.ExitCode)"; Get-Content $p3 | ForEach-Object { Write-Host "  $_" }

Write-Host '=== 反例：把版本管理器的落点全部指到空目录、PATH 清空（必须判"没有 node"）==='
$empty = Join-Path $out 'empty-home'
New-Item -ItemType Directory -Force -Path $empty | Out-Null
$env:PATH = 'C:\Windows\System32'
$env:FNM_DIR = $empty
$env:APPDATA = $empty
$env:LOCALAPPDATA = $empty
$env:USERPROFILE = $empty
$env:NVM_SYMLINK = $empty
$env:VOLTA_HOME = $empty
$p2 = Join-Path $out 'negative.txt'
$r2 = Start-Process -FilePath $exe -ArgumentList @('--selftest-node', $p2) -Wait -PassThru
Write-Host "exit=$($r2.ExitCode)"; Get-Content $p2 | ForEach-Object { Write-Host "  $_" }
