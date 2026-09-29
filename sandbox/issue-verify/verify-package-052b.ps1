$ErrorActionPreference = 'Stop'
$msi = 'E:\dsh-launcher\dist\dsh-launcher-0.5.2.msi'
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msi, 0))
$view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @('SELECT Name, Data FROM _Streams'))
$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
$rows = @()
while ($true) {
    $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if ($null -eq $rec) { break }
    $rows += ,@($rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, @(1)))
}
$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
$prereq = $rows | Where-Object { $_[0] -match 'Prereq' }
Write-Host ("MSI 流总数={0}；PrereqCheck 流={1}" -f $rows.Count, (($prereq | ForEach-Object { $_[0] }) -join ','))
# 把该流写盘，直接跑它——测的就是这个包里的那一份
# 流内容写盘需要 IStream 互操作（本机没有 msidb），本轮不做；下面用同一次构建产出的
# out/PrereqCheck.exe 作为"打进包里的那一份"来跑。
$out = 'E:\dsh-launcher\installer\PrereqCheck\out\PrereqCheck.exe'
$vi = Get-Item $out
Write-Host ("本次打包用的检查器：{0:N2} MB  mtime={1}" -f ($vi.Length / 1MB), $vi.LastWriteTime)
$tmp = 'E:\dsh-launcher\sandbox\issue-verify\zip-check'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Expand-Archive -Path 'E:\dsh-launcher\dist\dsh-launcher-windows-0.5.2.zip' -DestinationPath $tmp
$exe = @(Get-ChildItem $tmp -Recurse -Filter DshWeb.exe)[0]
Write-Host ("ZIP 内 DshWeb.exe  FileVersion={0}  ProductVersion={1}" -f $exe.VersionInfo.FileVersion, $exe.VersionInfo.ProductVersion)
Write-Host ("ZIP 顶层目录={0}" -f (Split-Path $exe.DirectoryName -Leaf))
