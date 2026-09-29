param([int]$BudgetSeconds = 180)
# Wait for the launcher to reach HEALTHY, then print the facts a #28-② run needs: the URL the shell
# navigated to (contains the fresh token), the attached service pid, and every window it owns.
$ErrorActionPreference = 'Continue'
. 'E:\dsh-launcher\sandbox\issue-verify\lib.ps1'
$p = Get-Process DshWeb -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { 'no launcher'; exit 1 }
$lg = Join-Path $env:USERPROFILE '.dsh\dsh-launcher\dsh.log'
$deadline = (Get-Date).AddSeconds($BudgetSeconds)
$hit = ''
while ((Get-Date) -lt $deadline) {
  $mine = @(Get-Content $lg -Encoding UTF8 | Where-Object { $_ -like ('*"pid":' + $p.Id + ',*') })
  if (($mine -join "`n") -match 'HEALTHY') { $hit = 'HEALTHY'; break }
  if (($mine -join "`n") -match 'readiness failed') { $hit = 'readiness failed'; break }
  Start-Sleep -Milliseconds 1500
}
Write-Host ("launcher pid={0} verdict={1}" -f $p.Id, $hit)
$mine | Where-Object { $_ -match 'service start via identity|process layer attached|navigating main web|HEALTHY|update notice|notice displayed' } |
  Select-Object -Last 8 | ForEach-Object { Write-Host $_.Substring(0, [Math]::Min(230, $_.Length)) }
Write-Host '--- windows ---'
foreach ($w in @(Q-FindWindows -Ctx @{ Proc = $p } -Title '' -Any)) {
  Write-Host ("[{0}] {1}x{2} at ({3},{4})" -f $w.Title, $w.W, $w.Ht, $w.X, $w.Y)
}
$srv = @(Get-NetTCPConnection -LocalPort 3080 -State Listen -ErrorAction SilentlyContinue |
  Select-Object -ExpandProperty OwningProcess | Select-Object -First 1)
Write-Host ("service pid on 3080 = " + ($srv -join ','))
