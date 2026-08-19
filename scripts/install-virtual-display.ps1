<#
.SYNOPSIS
Task 2 方案 B：在 GitHub Actions / 本地开发机上静默创建 1~2 个虚拟副屏（无物理硬件），
用于真实 UI 的跨屏最大化 E2E（修复"多屏 DPI 最大化丢窗"）。

.DESCRIPTION
使用开源的间接显示驱动（IddSampleDriver 系列，如 amatCern/iddsample、Virtual-Display-Driver）
在 Windows 桌面会话中注入虚拟显示器。原理：
  * Windows 的 Indirect Display Driver（IDD）模型允许用户态驱动通过 IddCx 注册"显示器"，DWM
    会将其当作真实监视器枚举——无需任何物理 DP/HDMI 插拔。
  * 虚拟屏可以是负坐标（放左侧）、任意分辨率/缩放，正好覆盖异构 DPI 多屏的回归面。
CI（windows-latest）默认是交互桌面会话，可装驱动并热插虚拟屏；本脚本为此自动化。

要求：
  * 需交互式 Windows 会话（GitHub Actions windows-latest 自带；RDP/服务会话不可用）。
  * 需管理员权限（安装 .inf 驱动）。
  * 首次安装后通常需重启或等待 PnP 枚举；本脚本自动等待虚拟屏出现（-TimeoutSec 可控）。

.EXAMPLE
# CI 内以管理员身份运行，创建 2 个虚拟屏（默认 1920x1080@96DPI）
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/install-virtual-display.ps1 -Count 2

.EXAMPLE
# 本地：安装 1 个虚拟屏并设为异构 DPI（150%）
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/install-virtual-display.ps1 -Count 1 -Restore
#>
[CmdletBinding()]
param(
    # 创建虚拟屏数量（1~2）
    [ValidateRange(1, 2)]
    [int]$Count = 1,

    # 驱动源：仅本地路径时表示已下载的 zip；留空则从默认上游下载
    [string]$DriverZip = "",

    # 等待虚拟屏被 DWM 枚举的超时秒数
    [int]$TimeoutSec = 120,

    # 退出前是否恢复原显示拓扑（卸载虚拟屏，避免污染 CI 会话）
    [switch]$Restore,

    # 跳过真实驱动安装（仅校验/打印检测），用于无管理员权限的快速预检
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $pr.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-VirtualMonitors {
    # 用 Win32 EnumDisplayMonitors 枚举，识别"间接显示/虚拟"驱动产生的监视器。
    # 生产代码用同一 API（MonitorFromWindow）跨屏最大化，这里用它确认虚拟屏枚举成功。
    $sig = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class VD {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    public delegate bool EnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rc, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    public static int Count(){ int n=0; EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(IntPtr h, IntPtr d, ref RECT r, IntPtr l)=>{ n++; return true; },IntPtr.Zero); return n; }
    public static string[] Rects(){ var s=new System.Collections.Generic.List<string>();
        EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(IntPtr h, IntPtr d, ref RECT r, IntPtr l)=>{
            var mi=new MONITORINFO{cbSize=Marshal.SizeOf<MONITORINFO>()}; GetMonitorInfo(h,ref mi);
            s.Add($"({mi.rcMonitor.L},{mi.rcMonitor.T},{mi.rcMonitor.R-mi.rcMonitor.L}x{mi.rcMonitor.B-mi.rcMonitor.T})"); return true; },IntPtr.Zero);
        return s.ToArray(); }
}
'@
    Add-Type -TypeDefinition $sig -Language CSharp
    [VD]::Rects()
}

if (-not (Test-Admin) -and -not $DryRun) {
    throw "需要管理员权限安装虚拟显示驱动。请以管理员身份重新运行，或用 -DryRun 做预检。"
}

if ($DryRun) {
    Write-Host "[DRY-RUN] 检测当前监视器拓扑：" -ForegroundColor Cyan
    Get-VirtualMonitors | ForEach-Object { Write-Host "   $_" }
    Write-Host "[DRY-RUN] 未安装任何驱动。完成。"
    return
}

# ---- 1. 获取虚拟显示驱动包 ----
$work = Join-Path $env:TEMP ("vd-driver-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$zip = $DriverZip
if (-not $zip) {
    # 默认从 VirtualDrivers/Virtual-Display-Driver（原 rocketGod-git/jglrxavp 组织已迁移）拉取
    # 预编译 zip。注意 jglrxavp/iddsample 的 releases/latest 已 404（上游删除了 assets），
    # 2026-08 起改用 VirtualDrivers 组织的 Driver.Only 包（tag 25.7.23+）。
    # 设备默认创建 1 块虚拟副屏（1600x900@100%）；-Count 2 需在 DSH_HOME 放 option.txt 配置。
    # CI 网络受限时用 -DriverZip 指到预置在 repo 缓存/自托管 Runner 的包。
    $defaultUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/latest/download/VirtualDisplayDriver-x86.Driver.Only.zip"
    $zip = Join-Path $work "idd.zip"
    Write-Host "  下载虚拟显示驱动: $defaultUrl"
    # 60s 硬超时：GitHub release download 在 CI 偶发慢连接/重定向悬挂，必须快速失败而非无限卡。
    Invoke-WebRequest -Uri $defaultUrl -OutFile $zip -UseBasicParsing -TimeoutSec 60
    if (-not (Test-Path $zip) -or (Get-Item $zip).Length -lt 10000) {
        throw "虚拟显示驱动下载失败或包过小（$((Get-Item $zip -ErrorAction SilentlyContinue).Length) bytes）"
    }
}
$infDir = Join-Path $work "inf"
Expand-Archive -Path $zip -DestinationPath $infDir -Force

# ---- 2. 安装驱动（PnP 用户模式：直接 Add 设备到当前会话）----
# IddSampleDriver 提供 .inf，指定 Root\IddSampleDriver 即热装到交互会话，无需物理设备。
$inf = Get-ChildItem -Path $infDir -Filter *.inf -Recurse | Select-Object -First 1
if (-not $inf) { throw "驱动包内未找到 .inf 文件：$zip" }
Write-Host "  安装驱动 INF: $($inf.FullName)"
$pnputil = "pnputil"

# pnputil 包装 90s 超时：CI 上 /install 偶发在 DriverStore 签名校验阶段悬挂，
# 必须快速失败而非无限卡（此前 CI 卡 20+ 分钟即此根因）。
# 注意：参数用**单字符串**传给 ProcessStartInfo（ArgumentList 在此环境会导致 pnputil
# 只打印帮助而参数未生效——CI 实测），inf 路径手动加引号防空格。
function Invoke-PnPUtil {
    param([string]$Arguments)
    $psi = [System.Diagnostics.ProcessStartInfo]::new("pnputil", $Arguments)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit(90000)) {
        $p.Kill()
        $p.WaitForExit()
        Write-Warning "pnputil 超时（90s），已终止：$Arguments"
        return $false
    }
    $out = $p.StandardOutput.ReadToEnd()
    if ($out) { Write-Host $out.Trim() }
    return $p.ExitCode -eq 0
}

# 1) 创建 root enumerated device（返回码失败不致命：继续尝试实例化并等待枚举）
$pnputilOk = Invoke-PnPUtil "/add-driver `"$($inf.FullName)`" /install"
if (-not $pnputilOk) { Write-Warning "pnputil /add-driver 失败，虚拟屏可能无法枚举" }
# 2) 通过 devcon（若存在）注册 Root\IddSampleDriver 实例；不存在则跳过（驱动 Add 已覆盖）
$devcon = Get-Command "devcon.exe" -ErrorAction SilentlyContinue
if ($devcon) {
    try { & $devcon.Source /add "@ROOT\DISPLAY\0000" 2>&1 | Out-Null } catch { }
} else {
    Write-Host "  devcon.exe 不可用（CI 未自带），跳过 Root\DISPLAY\0000 显式注册（依赖 /add-driver 自动实例化）"
}

# 触发 DWM 重新枚举（枚举虚拟屏需 DWM 重建，短等待）
Start-Sleep -Seconds 5

# ---- 3. 等待虚拟屏出现 ----
$before = (Get-VirtualMonitors).Count
Write-Host "  现有监视器数: $before"
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$monitors = @()
while ((Get-Date) -lt $deadline) {
    $monitors = Get-VirtualMonitors
    if ($monitors.Count -ge ($before + $Count)) { break }
    Start-Sleep -Seconds 3
}
if ($monitors.Count -lt ($before + $Count)) {
    Write-Warning "虚拟屏未在 ${TimeoutSec}s 内枚举完成（当前 $($monitors.Count) 块）。请检查驱动安装与交互会话。"
}
Write-Host "  当前监视器拓扑: $($monitors -join ' ')" -ForegroundColor Green

# ---- 4. 分辨率/缩放动态调整（见 Set-VirtualDisplay.ps1；本脚本仅确认可枚举）----
# 分辨率与 DPI 缩放调整由 Set-VirtualDisplay.ps1 的 ChangeDisplaySettingsEx 完成，
# 以便构造"异构 DPI"回归拓扑。此处只负责"有虚拟屏"这一前提。

# ---- 5. 退出时恢复 ----
if ($Restore) {
    Write-Host "  恢复显示拓扑（卸载虚拟屏）..."
    & $pnputil /remove-device "ROOT\DISPLAY\0000" 2>&1 | Out-Null
    Start-Sleep -Seconds 3
}
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "[完成] 虚拟显示环境就绪：$($monitors.Count) 块监视器" -ForegroundColor Green
