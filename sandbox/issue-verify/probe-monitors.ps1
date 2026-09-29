# One-off probe: physical monitor topology as the shell sees it (no app launch).
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'lib.ps1')
$ms = @(Q-Monitors)
foreach ($m in $ms) {
    Write-Host ("{0} primary={1} dpi={2} physWork=({3},{4}) {5}x{6} physFull=({7},{8}) {9}x{10} logical={11}" -f `
        $m.Device, $m.Primary, $m.Dpi, $m.X, $m.Y, $m.W, $m.H, $m.FullX, $m.FullY, $m.FullW, $m.FullH, $m.LogicalBounds)
}
[void][QWin]::SetCursorPos(100, 100); Start-Sleep -Milliseconds 200
Write-Host ("cursor after SetCursorPos(100,100) = " + (Q-PhysCursor))
