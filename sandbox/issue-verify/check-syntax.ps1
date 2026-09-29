# Parse-check the harness scripts without running them (fast fail before a 4-minute real run).
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$bad = 0
foreach ($f in @('lib.ps1', 'run.ps1')) {
    $errs = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $dir $f), [ref]$null, [ref]$errs)
    if ($errs) { $bad++; Write-Host "PARSE FAIL $f"; $errs | ForEach-Object { Write-Host ("  {0}: {1}" -f $_.Extent.StartLineNumber, $_.Message) } }
    else { Write-Host "parse ok: $f" }
}
. (Join-Path $dir 'lib.ps1')
Write-Host ("Q-Monitors -> {0} monitor(s)" -f (@(Q-Monitors).Count))
Write-Host ("Q-ChildWindows(0) -> {0}" -f (@(Q-ChildWindows ([IntPtr]::Zero)).Count))
exit $bad
