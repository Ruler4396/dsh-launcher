param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$SceneRoot = "$PSScriptRoot",
    [int]$BudgetSeconds = 45,
    [string]$Label = "size"
)
# Measure the notice card on the REAL desktop: window rect + per-window DPI + screenshot.
# ASCII-only on purpose: powershell.exe 5.1 mis-decodes BOM-less UTF-8 with Chinese text.
# Isolation: own DSH_HOME / WebView2 dir / random port, no real dsh service (splash delay
# short-circuits the startup pipeline). Cleanup by recorded PID only.
$ErrorActionPreference = 'Stop'
if (-not $SceneRoot) { $SceneRoot = $PSScriptRoot }
$scene = (Resolve-Path $SceneRoot).Path
Remove-Item (Join-Path $scene 'home-size'), (Join-Path $scene 'wv2-size') -Recurse -Force -ErrorAction SilentlyContinue
$tag = ''
if (Test-Path (Join-Path $scene 'home-size')) { $tag = '-' + (Get-Date -Format 'HHmmss') }
$home_ = Join-Path $scene "home-size$tag"
$wv2 = Join-Path $scene "wv2-size$tag"
$shot = Join-Path $scene ("staging\card-" + $Label + "-" + (Get-Date -Format 'HHmmss') + ".png")
New-Item -ItemType Directory -Path $home_, $wv2 | Out-Null

$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start()
$port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $Exe; $psi.WorkingDirectory = Split-Path $Exe; $psi.UseShellExecute = $false
foreach ($k in 'DSH_WEB_URL','DSH_HOME','DSH_TEST_NOTICE_CARD','DSH_TEST_INSTALL_MODE',
               'DSH_TEST_SPLASH_DELAY_MS','DSH_TEST_TOAST','DSH_TEST_FORCE_TOAST') {
    $psi.EnvironmentVariables.Remove($k) | Out-Null }
$psi.EnvironmentVariables['DSH_SANDBOX'] = '1'
$psi.EnvironmentVariables['DSH_HOME'] = $home_
$psi.EnvironmentVariables['DSH_WEB_PORT'] = "$port"
$psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = $wv2
$psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
$psi.EnvironmentVariables['DSH_TELEMETRY_DISABLED'] = '1'
$psi.EnvironmentVariables['DSH_TEST_SPLASH_DELAY_MS'] = '1500'   # no real service needed
$psi.EnvironmentVariables['DSH_TEST_NOTICE_CARD'] = '1'
$psi.EnvironmentVariables['DSH_TEST_INSTALL_MODE'] = 'msi'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public struct R { public int L, T, Rt, B; }
}
"@
function FindCards([int]$pid_) {
    $acc = New-Object System.Collections.ArrayList
    $cb = [W+EnumProc] { param($h, $l)
        if ([W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 64
            [void][W]::GetWindowText($h, $sb, $sb.Capacity)
            if ($sb.ToString() -eq 'DshNoticeCard') {
                $p = 0; [void][W]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $pid_) {
                    $r = New-Object W+R
                    if ([W]::GetWindowRect($h, [ref]$r)) {
                        [void]$acc.Add([pscustomobject]@{
                            H = $h; W = $r.Rt - $r.L; Ht = $r.B - $r.T
                            X = $r.L; Y = $r.T; Dpi = [W]::GetDpiForWindow($h) }) } } } }
        return $true }
    [void][W]::EnumWindows($cb, [IntPtr]::Zero)
    return $acc
}

$p = [System.Diagnostics.Process]::Start($psi)
Write-Host "pid=$($p.Id) port=$port home=$home_"
$deadline = (Get-Date).AddSeconds($BudgetSeconds)
$found = @()
while ((Get-Date) -lt $deadline) {
    $found = @(FindCards $p.Id)
    if ($found.Count -gt 0) { break }
    Start-Sleep -Milliseconds 400
}
if ($found.Count -eq 0) { Write-Host "!! no card window seen within ${BudgetSeconds}s" }
foreach ($c in $found) {
    Write-Host ("CARD dpi={0} w={1} h={2} at=({3},{4})" -f $c.Dpi, $c.W, $c.Ht, $c.X, $c.Y)
    $va = [System.Windows.Forms.SystemInformation]::VirtualScreen
    Write-Host ("     workarea-ish virtual screen = {0}x{1} at ({2},{3})" -f $va.Width, $va.Height, $va.X, $va.Y)
}
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "screenshot -> $shot"

$sysRoot = if ($env:SystemRoot) { $env:SystemRoot } else { 'C:\Windows' }
& (Join-Path $sysRoot 'System32\taskkill.exe') /T /F /PID $p.Id | Out-Null
Start-Sleep -Seconds 2
Write-Host ("cleanup: left={0}" -f (@(Get-Process -Id $p.Id -ErrorAction SilentlyContinue).Count))

$log = Join-Path $home_ 'dsh-launcher\dsh.log'
Write-Host "---- in-process geometry (dsh.log) ----"
if (Test-Path $log) {
    Get-Content $log | Where-Object { $_ -match 'notice displayed|notice card self-test' } | ForEach-Object { Write-Host $_ }
} else { Write-Host "(no log)" }
