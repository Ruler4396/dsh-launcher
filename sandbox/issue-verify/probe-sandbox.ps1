$ErrorActionPreference = 'SilentlyContinue'
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Write-Host ("版本: {0}  EditionID={1}  build={2}" -f $cv.ProductName, $cv.EditionID, $cv.CurrentBuild)
Write-Host ("WindowsSandbox.exe 存在: {0}" -f (Test-Path "$env:SystemRoot\System32\WindowsSandbox.exe"))
Write-Host ("WindowsSandboxClient.exe 存在: {0}" -f (Test-Path "$env:SystemRoot\System32\WindowsSandboxClient.exe"))
Write-Host ("SandboxWorkspaces runtime 目录存在: {0}" -f (Test-Path "$env:SystemRoot\System32\ContaineerHost*"))
$cs = Get-CimInstance Win32_ComputerSystem
Write-Host ("HyperVPresent: {0}" -f $cs.HyperVisorPresent)
$cpu = Get-CimInstance Win32_Processor
Write-Host ("CPU 虚拟化: VirtualizationFirmwareEnabled={0} SecondLevelAddressTranslation={1} DataExecutionPrevention={2}" -f `
    $cpu.VirtualizationFirmwareEnabled, $cpu.SecondLevelAddressTranslationExtensions, $cpu.DataExecutionPreventionAvailable)
Write-Host ("服务 vmcompute(HostComputeService): {0}" -f (Get-Service vmcompute).Status)
Write-Host ("服务 VMMS(Hyper-V Virtual Machine Mgmt): {0}" -f (Get-Service VMMS).Status)
Write-Host ("功能查询（非管理员可能拒绝）:")
try {
    $f = Get-WindowsOptionalFeature -Online -FeatureName 'Containers-DisposableClientVM' -ErrorAction Stop
    Write-Host ("  Containers-DisposableClientVM = {0}" -f $f.State)
} catch {
    Write-Host ("  查不到：{0}" -f $_.Exception.Message)
}
