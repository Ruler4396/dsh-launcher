$ErrorActionPreference = 'Stop'
$files = @(
    'E:\dsh-launcher\sandbox\issue-verify\validate2.msi',
    'E:\dsh-launcher\sandbox\issue-verify\new-checker.msi',
    'E:\dsh-launcher\sandbox\issue-verify\msi-v0.5.1\dsh-launcher-0.5.1.msi'
)
$installer = New-Object -ComObject WindowsInstaller.Installer
foreach ($f in $files) {
    if (-not (Test-Path $f)) { Write-Host "MISSING $f"; continue }
    try {
        $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($f, 0))
        $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @('SELECT Name FROM _Streams'))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $names = @()
        while ($true) {
            $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if ($null -eq $rec) { break }
            $names += $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, @(1))
        }
        Write-Host ("VALID   {0}  streams={1}  PrereqCheck={2}" -f (Split-Path $f -Leaf), $names.Count,
            (($names | Where-Object { $_ -match 'Prereq' }) -join ','))
    } catch {
        Write-Host ("INVALID {0} -> {1}" -f (Split-Path $f -Leaf), $_.Exception.Message)
    }
}
