$a = [Reflection.Assembly]::LoadFrom('E:\dsh-launcher\src\DshShell\bin\Debug\net10.0-windows\DshWeb.dll')
$t = $a.GetType('DshWeb.Win32.MonitorDpi')
"MonitorDpi type: $($t -ne $null)"
foreach ($pt in @(@(960,540), @(0,0), @(1900,1000))) {
  $p = [Activator]::CreateInstance([Drawing.Point])
  $p.X = $pt[0]; $p.Y = $pt[1]
  $r = $t.GetMethod('ForPoint', [type[]]@([Drawing.Point])).Invoke($null, @($p))
  "ForPoint($($pt[0]),$($pt[1])) = $r"
}
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Raw {
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(P pt, uint f);
  [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr m, int t, out uint x, out uint y);
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
}
'@
$x=0; $y=0
$m0 = [Raw]::MonitorFromPoint([Raw+P]::new(960,540), 2)
"MonitorFromPoint hwnd=$m0"
foreach ($kind in 0,1,2) {
  $hr = [Raw]::GetDpiForMonitor($m0, $kind, [ref]$x, [ref]$y)
  "GetDpiForMonitor(type=$kind) hr=$hr x=$x y=$y"
}
"GetDpiForSystem=$([Raw]::GetDpiForSystem())  GetDpiForWindow(desktop)=$([Raw]::GetDpiForWindow([Raw]::GetDesktopWindow()))"
