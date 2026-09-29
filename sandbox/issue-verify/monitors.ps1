param([switch]$Json)
# Per-monitor effective DPI + physical geometry, from the same APIs the shell uses.
$ErrorActionPreference = 'Stop'
if (-not ('MonDpi' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class MonDpi {
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumProc cb, IntPtr data);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr h, ref MI mi);
  [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr h, int t, out uint x, out uint y);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool EnumDisplaySettingsW(string dev, int mode, out DEVMODE dm);
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int cb; public R rcM; public R rcW; public int flags; }
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion; public short dmSize, dmDriverExtra;
    public int dmFields; public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTogOption;
    public short dmFormSize, dmVScanCount, dmVRefreshRate;
    public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public long dmDisplayTime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst=12)] public int[] dmPanningWidth;
  }
}
"@
}
$list = New-Object System.Collections.ArrayList
$cb = [MonDpi+EnumProc] { param($h, $l)
    $mi = New-Object MonDpi+MI
    $mi.cb = [System.Runtime.InteropServices.Marshal]::SizeOf($mi)
    [void][MonDpi]::GetMonitorInfoW($h, [ref]$mi)
    $dx = 0; $dy = 0
    $hr = [MonDpi]::GetDpiForMonitor($h, 0, [ref]$dx, [ref]$dy)
    $prim = ($mi.flags -band 1) -ne 0
    $eff = if ($hr -eq 0) { [int]$dx } else { "hr=$hr" }
    $pct = if ($hr -eq 0) { [int](($dx * 100) / 96) } else { '?' }
    [void]$list.Add([pscustomobject]@{
        Primary = $prim
        RcMonitor = ("{0},{1} {2}x{3}" -f $mi.rcM.L, $mi.rcM.T, ($mi.rcM.Rt-$mi.rcM.L), ($mi.rcM.B-$mi.rcM.T))
        RcWork    = ("{0},{1} {2}x{3}" -f $mi.rcW.L, $mi.rcW.T, ($mi.rcW.Rt-$mi.rcW.L), ($mi.rcW.B-$mi.rcW.T))
        EffDpi    = $eff
        ScalePct  = $pct
    })
    return $true }
[void][MonDpi]::EnumDisplayMonitors([IntPtr]::Zero, [IntPtr]::Zero, $cb, [IntPtr]::Zero)
$list | Format-Table -AutoSize | Out-String -Width 160
Add-Type -AssemblyName System.Windows.Forms
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
    Write-Host ("winforms {0} bounds={1} work={2} primary={3}" -f $s.DeviceName, $s.Bounds, $s.WorkingArea, $s.Primary)
}
