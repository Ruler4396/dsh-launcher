$ErrorActionPreference = 'Continue'
# Read-only diagnosis: what is on screen right now + what the shell logged for it.
# ASCII-only source (pwsh 5.1 mis-decodes BOM-less UTF-8); prints raw JSON lines (Chinese may be
# \uXXXX escaped by the logger, so anchors for filtering stay ASCII).
Add-Type -AssemblyName System.Windows.Forms
$exePaths = @{}
foreach ($p in @(Get-Process DshWeb -ErrorAction SilentlyContinue)) {
  $exePaths[$p.Id] = $p.MainModule.FileName
  Write-Host ("proc {0} title=[{1}] responding={2} start={3}" -f $p.Id, $p.MainWindowTitle, $p.Responding, $p.StartTime)
}
foreach ($p in @(Get-Process node -ErrorAction SilentlyContinue)) {
  Write-Host ("node pid={0} start={1}" -f $p.Id, $p.StartTime)
}
Write-Host ("port 3080 listeners: " + ((Get-NetTCPConnection -LocalPort 3080 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess) -join ','))

# Every top-level window of the launcher pids, including #32770 message boxes.
$sig = @'
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections;
public static class WinEnum {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, Rt, B; }
  public static ArrayList Of(int[] pids) {
    var acc = new ArrayList();
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      foreach (int want in pids) {
        if (pid == want && IsWindowVisible(h)) {
          var t = new StringBuilder(256); GetWindowText(h, t, 256);
          var c = new StringBuilder(128); GetClassName(h, c, 128);
          RECT r; GetWindowRect(h, out r);
          acc.Add(pid + " | " + c + " | [" + t + "] | " + (r.Rt - r.L) + "x" + (r.B - r.T));
        }
      }
      return true;
    }, IntPtr.Zero);
    return acc;
  }
}
'@
if (-not ('WinEnum' -as [type])) { Add-Type -TypeDefinition $sig }
$ids = @($exePaths.Keys | ForEach-Object { [int]$_ })
if ($ids.Count -gt 0) {
  Write-Host "--- visible top-level windows of the launcher ---"
  foreach ($w in [WinEnum]::Of($ids)) { Write-Host $w }
}

$lg = Join-Path $env:USERPROFILE '.dsh\dsh-launcher\dsh.log'
Write-Host "--- last log lines mentioning the startup/safe-mode chain ---"
Get-Content $lg -Encoding UTF8 -Tail 260 | Where-Object {
  $_ -match 'SAFEMODE|safe-mode|readiness|E2010|E2007|E2002|Ask|answered|notice|service start|exited|profile|health'
} | Select-Object -Last 34 | ForEach-Object { Write-Host $_ }
