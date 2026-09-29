param([int]$Pid_ = 0, [int]$BudgetSeconds = 150)
# Read-only check of the REAL user home log: did the pending dsh update get applied on restart?
# ASCII-only source (pwsh 5.1 mis-decodes BOM-less UTF-8). No file writes, no process control.
$lg = Join-Path $env:USERPROFILE '.dsh\dsh-launcher\dsh.log'
if (-not (Test-Path $lg)) { "no log at $lg"; exit }
$deadline = (Get-Date).AddSeconds($BudgetSeconds)
$pat = '"pid":' + $Pid_ + ','
$settled = $false
while ((Get-Date) -lt $deadline) {
  $mine = @(Get-Content $lg -Encoding UTF8 | Where-Object { $_ -like ('*' + $pat + '*') })
  $joined = $mine -join "`n"
  if ($joined -match 'HEALTHY' -or $joined -match 'readiness failed' -or $joined -match 'E2010') {
    $settled = $true; break }
  Start-Sleep -Milliseconds 1500
}
Write-Host ("settled={0} ownLogLines={1}" -f $settled, $mine.Count)
$mine | Where-Object {
  $_ -match 'update|pending|apply|applied|version|start target|identity|HEALTHY|readiness|SAFEMODE|staging|runtimes'
} | ForEach-Object { Write-Host $_ }
