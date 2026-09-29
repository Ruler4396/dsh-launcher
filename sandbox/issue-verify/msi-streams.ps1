$ErrorActionPreference = 'Stop'
$msi = 'E:\dsh-launcher\sandbox\issue-verify\msi-v0.5.1\dsh-launcher-0.5.1.msi'
$exe = 'E:\dsh-launcher\sandbox\issue-verify\msi-v0.5.1\extracted\PFiles64\dsh-launcher\DshWeb.exe'
$v = (Get-Item $exe).VersionInfo
Write-Host ("抽出的 DshWeb.exe: FileVersion={0} ProductVersion={1}" -f $v.FileVersion, $v.ProductVersion)
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msi, 0))
$view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Name FROM _Streams"))
$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
$names = @()
while ($true) {
    $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if ($null -eq $rec) { break }
    $names += $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, @(1))
}
$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
Write-Host ("MSI 内嵌流：{0}" -f ($names -join ', '))
Write-Host ("含 PrereqCheck 自定义动作流：{0}" -f (($names | Where-Object { $_ -match 'Prereq' }).Count -gt 0))
