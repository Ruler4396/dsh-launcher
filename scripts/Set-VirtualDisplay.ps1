<#
.SYNOPSIS
Task 2 方案 B：用 ChangeDisplaySettingsEx 动态调整指定虚拟显示器的分辨率与缩放（DPI），
构造"异构 DPI 多屏"回归拓扑，供真实 UI 的跨屏最大化 E2E 使用。

.DESCRIPTION
在已通过 install-virtual-display.ps1 注入虚拟屏的基础上，把某块虚拟屏改成指定
分辨率 + 缩放（如 1920x1080 @ 150% = 物理 2880x1620）。核心是调用原生
ChangeDisplaySettingsExW：
  1. EnumDisplaySettingsExW 拿到目标设备当前的 DEVMODE 模板；
  2. 改 dmPelsWidth/dmPelsHeight（物理像素）与 LOGPIXELSX/Y（缩放 96=100%,144=150%）；
  3. ChangeDisplaySettingsExW(设备名, DEVMODE, HWND_BROADCAST, CDS_UPDATEREGISTRY) 应用。
CDS_UPDATEREGISTRY 会把设置写进注册表，会话重启后仍生效——CI 每次 job 用 -Restore 还原。

为什么需要它：WM_GETMINMAXINFO 的物理/逻辑像素错位只在"异构 DPI"下才暴露。若两屏都 100%，
逻辑==物理，Bug 不触发。本脚本把副屏设成不同缩放，才能真实复现"最大化丢窗"。

.EXAMPLE
# 把 2 号虚拟屏设为 2560x1440 @ 150%（物理），构造异构 DPI
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Set-VirtualDisplay.ps1 -DeviceIndex 1 -Width 2560 -Height 1440 -ScalePercent 150

.EXAMPLE
# 还原所有虚拟屏为系统默认
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Set-VirtualDisplay.ps1 -Restore
#>
[CmdletBinding()]
param(
    # 虚拟屏索引（0=主屏，1=第 1 块虚拟屏，2=第 2 块……）
    [ValidateRange(0, 3)]
    [int]$DeviceIndex = 1,

    # 物理分辨率（宽/高，px）
    [int]$Width = 1920,
    [int]$Height = 1080,

    # 缩放百分比（96=100%, 120=125%, 144=150%, 192=200%）
    [ValidateRange(96, 384)]
    [int]$ScalePercent = 96,

    [switch]$Restore
)

$ErrorActionPreference = "Stop"

$sig = @'
using System;
using System.Runtime.InteropServices;

public static class CDS {
    // DEVMODE 关键字段（仅用到的偏移；其余填充保留）
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    public delegate bool MonitorEnum(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rc, MonitorEnum cb, IntPtr lp);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettingsEx(string? devName, uint modeNum, ref DEVMODE mode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string? devName, ref DEVMODE mode, IntPtr hwnd, uint flags, IntPtr lp);

    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint DISP_CHANGE_SUCCESSFUL = 0;

    // 按索引取监视器句柄（0=主屏，1..=副屏）
    public static IntPtr[] MonitorHandles() {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h,d,r,l) => { list.Add(h); return true; }, IntPtr.Zero);
        return list.ToArray();
    }
}
'@
Add-Type -TypeDefinition $sig -Language CSharp

# ---- 按设备索引定位显示设备名 ----
# EnumDisplayDevices 在结构体里更繁琐；这里用 EnumDisplaySettingsEx 无参遍历"所有设备名"
# 并不直接。为稳妥：用 .NET 的 System.Windows.Forms.Screen（仅取设备名，不取逻辑坐标）列设备。
Add-Type -AssemblyName System.Windows.Forms
$screens = [System.Windows.Forms.Screen]::AllScreens
if ($DeviceIndex -ge $screens.Count) {
    throw "设备索引 $DeviceIndex 超出可用监视器（共 $($screens.Count)）。请先 install-virtual-display.ps1 建虚拟屏。"
}
$devName = $screens[$DeviceIndex].DeviceName
Write-Host "  目标设备: $devName ($($screens[$DeviceIndex].Bounds))" -ForegroundColor Cyan

if ($Restore) {
    # 用空 DEVMODE + CDS_UPDATEREGISTRY 让系统回读注册表默认
    $mode = New-Object CDS+DEVMODE
    $mode.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][CDS+DEVMODE])
    $r = [CDS]::ChangeDisplaySettingsEx($devName, [ref]$mode, [IntPtr]::Zero, 0, [IntPtr]::Zero)
    Write-Host "  还原 $devName 到系统默认 (ChangeDisplaySettingsEx=$r)"
    return
}

# ---- 取当前 DEVMODE 模板并改写分辨率/缩放 ----
$mode = New-Object CDS+DEVMODE
$mode.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][CDS+DEVMODE])
if (-not [CDS]::EnumDisplaySettingsEx($devName, 0xFFFFFFFF, [ref]$mode, 0)) {  # 0xFFFFFFFF = ENUM_CURRENT_SETTINGS
    throw "EnumDisplaySettingsEx 失败：$devName"
}
$mode.dmFields = 0x00080000 -bor 0x00100000 -bor 0x00400000  # DM_PELSWIDTH | DM_PELSHEIGHT | DM_LOGPIXELS
$mode.dmPelsWidth = [uint32]$Width
$mode.dmPelsHeight = [uint32]$Height
$mode.dmLogPixels = [uint16]$ScalePercent  # 96=100%, 144=150%, 192=200%（LOGPIXELS 即"每逻辑英寸像素"）

$result = [CDS]::ChangeDisplaySettingsEx($devName, [ref]$mode, [IntPtr]::Zero, [CDS]::CDS_UPDATEREGISTRY, [IntPtr]::Zero)
if ($result -ne [CDS]::DISP_CHANGE_SUCCESSFUL) {
    throw "ChangeDisplaySettingsEx 返回 0x$('{0:X}' -f $result)（非 0=成功）。分辨率/缩放组合可能不被虚拟屏驱动支持。"
}
Write-Host "  $devName -> ${Width}x${Height} @ ${ScalePercent}% (物理 ${Width}x${Height}, 逻辑 $([math]::Round($Width*96.0/$ScalePercent))x$([math]::Round($Height*96.0/$ScalePercent)))" -ForegroundColor Green
Start-Sleep -Seconds 3
