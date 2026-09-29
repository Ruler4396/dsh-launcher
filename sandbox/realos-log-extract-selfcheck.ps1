$ErrorActionPreference = 'Stop'
$log = Join-Path $env:TEMP 'realos-selfcheck.log'
@(
  'Test run for x.dll',
  '[xUnit.net 00:00:57.70]     DshShell.Tests.Lifecycle.BootHealthMonitorRealOsTests.RealOs_BootMonitor_RealProcessNonZeroExit_CapturedWithCode [FAIL]',
  '  失败 RealOs_BootMonitor_RealProcessNonZeroExit_CapturedWithCode [20 s]',
  '  错误消息:',
  '   Assert.Contains() Failure: Sub-string not found',
  '  字符串: "pid exit code=unavailable"，未找到: "7"',
  '',
  'Failed!  - 失败: 1，通过: 43'
) | Set-Content -Path $log -Encoding utf8
# —— 与 .github/workflows/realos-test.yml 里的收口逻辑逐字一致 ——
$lines = @(Get-Content $log)
$out = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match '\[FAIL\]') {
    $end = [Math]::Min($i + 16, $lines.Count - 1)
    foreach ($l in $lines[$i..$end]) { $out += ("::error file=tests::" + $l.Trim()) }
  }
}
$out += (Select-String -Path $log -Pattern 'Failed!|失败!' | ForEach-Object { "::error file=tests::" + $_.Line.Trim() })
$out | ForEach-Object { Write-Output $_ }
Write-Output ("SELFCHK emitted=" + $out.Count + " hasReason=" + [bool]($out -match 'Sub-string not found'))
if ($out.Count -lt 6 -or -not ($out -match 'Sub-string not found')) { exit 1 }
Write-Output 'SELFCHK PASS'
