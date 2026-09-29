$ErrorActionPreference = 'Stop'
$dist = 'E:\dsh-launcher\dist'
Write-Host '=== 产物清单与 SHA256 ==='
foreach ($f in Get-ChildItem $dist -File | Where-Object { $_.Name -match '0\.5\.2|SHA256SUMS' }) {
    Write-Host ("  {0,-40} {1,7:N1} MB" -f $f.Name, ($f.Length / 1MB))
}
$sums = Get-Content (Join-Path $dist 'SHA256SUMS.txt')
foreach ($line in $sums) {
    $parts = ($line -split '\s+')
    $name = $parts[-1]; $want = $parts[0]
    $p = Join-Path $dist $name
    if (Test-Path $p) {
        $got = (Get-FileHash $p -Algorithm SHA256).Hash.ToLower()
        Write-Host ("  {0}: 校验和一致={1}" -f $name, ($got -eq $want.ToLower()))
    } else { Write-Host "  $name：SHA256SUMS 里登记了但文件不存在" }
}
Write-Host '=== MSI 内嵌流 ==='
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer,
    @((Join-Path $dist 'dsh-launcher-0.5.2.msi'), 0))
$view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @('SELECT Name FROM _Streams'))
$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
$names = @()
while ($true) {
    $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if ($null -eq $rec) { break }
    $names += $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, @(1))
}
Write-Host ("  PrereqCheck 流存在={0}（{1}）" -f (($names | Where-Object { $_ -match 'Prereq' }).Count -gt 0),
    (($names | Where-Object { $_ -match 'Prereq' }) -join ','))
Write-Host '=== ZIP 里的壳版本 ==='
$zip = Join-Path $dist 'dsh-launcher-windows-0.5.2.zip'
$tmp = 'E:\dsh-launcher\sandbox\issue-verify\zip-check'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Expand-Archive -Path $zip -DestinationPath $tmp
$exe = @(Get-ChildItem $tmp -Recurse -Filter DshWeb.exe)[0]
Write-Host ("  {0}  FileVersion={1}  ProductVersion={2}" -f $exe.Name, $exe.VersionInfo.FileVersion, $exe.VersionInfo.ProductVersion)
$pre = @(Get-ChildItem $tmp -Recurse -Filter PrereqCheck.exe -ErrorAction SilentlyContinue)
Write-Host ("  ZIP 内是否含 PrereqCheck.exe：{0}" -f ($pre.Count -gt 0))
