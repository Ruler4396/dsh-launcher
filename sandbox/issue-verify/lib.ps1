# Shared harness for the issue-verification runs (T1..T11).
# ASCII-only source: powershell.exe 5.1 mis-decodes BOM-less UTF-8 and the parser then
# fails with misleading errors. Run under pwsh (7).
# Discipline: isolated DSH_HOME + own WebView2 dir + random port, hard wall-clock budgets,
# cleanup by recorded PID only (never by image name), module scan limited to the target PID.
$ErrorActionPreference = 'Stop'
$script:VerifyRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:ExePath = if ($env:DSH_VERIFY_EXE) { $env:DSH_VERIFY_EXE } else {
    'E:\dsh-launcher\src\DshShell\bin\Debug\net10.0-windows\DshWeb.exe'
}
if (-not (Test-Path $script:ExePath)) { throw "exe not found: $script:ExePath" }

if (-not ('QWin' -as [type])) {
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class QWin {
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(
      IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr res);
  public static IntPtr PackPoint(int x, int y) {
      unchecked { return new IntPtr((int)(((uint)((ushort)y) << 16) | (uint)((ushort)x))); }
  }
  /// <summary>问窗口自身"这个屏幕点算哪儿"（HTCAPTION=2 / HTCLIENT=1 / HTBORDER=17…）。
  /// 跨屏后自绘标题栏随 DPI 变高，硬编码 y+16 可能落在阴影边框上——只有问它本身才不会点空。</summary>
  public static int HitTest(IntPtr h, int x, int y, uint timeoutMs) {
      IntPtr res;
      SendMessageTimeout(h, 0x0084, IntPtr.Zero, PackPoint(x, y), 0x0002 /*SMTO_NORMAL*/, timeoutMs, out res);
      return (int)(long)res;
  }
  public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
}
"@
}

if (-not ('QMon' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class QMon {
  [StructLayout(LayoutKind.Sequential)] public struct Rc { public int L, T, Rt, B; }
  [StructLayout(LayoutKind.Sequential)] public struct Pt { public int X, Y; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct Info {
    public int cbSize; public Rc rcMonitor; public Rc rcWork; public int dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
  }
  public delegate bool MonProc(IntPtr hMon, IntPtr hdc, IntPtr pRect, IntPtr data);
  [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonProc cb, IntPtr data);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref Info mi);
  [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMon, int dpiType, out uint x, out uint y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out Pt p);
}
"@
}

if (-not ('QDpi' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class QDpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("shcore.dll")] public static extern int GetProcessDpiAwareness(IntPtr h, out int a);
  [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
}
"@
}

# 默认 pwsh 进程是 DPI-unaware：Windows 把 175% 副屏的 2560x1600 虚拟化成 1463x914 报给我们，
# 于是 GetWindowRect 的读数与壳（PerMonitorV2，物理像素）不同源，跨屏尺寸断言必然失真。
# T11 之前先把自己升到 PMv2，让双方共用同一套物理坐标系。
if ($env:DSH_VERIFY_PMAWARE -ne '0') {
    $a0 = 0; [void][QDpi]::GetProcessDpiAwareness([QDpi]::GetCurrentProcess(), [ref]$a0)
    $ok = [QDpi]::SetProcessDpiAwarenessContext([IntPtr](-4))
    $a1 = 0; [void][QDpi]::GetProcessDpiAwareness([QDpi]::GetCurrentProcess(), [ref]$a1)
    Write-Host "  harness dpi awareness: before=$a0 set(PMv2)=$ok after=$a1"
}

function Q-Shot {
    param($Ctx, [string]$Name)
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
    $p = Join-Path $Ctx.Shots ($Name + ".png")
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "  shot -> $p"
    return $p
}

function Q-NewScene {
    param([string]$Name, [hashtable]$ExtraEnv, [switch]$SafeModeSticky, [switch]$NoSafeProfile,
          [switch]$ExternalService, [int]$SplashDelayMs = 0, [switch]$Reuse, [string]$Arguments = '')
    $dir = Join-Path $VerifyRoot "scene-$Name"
    # -Reuse：跨进程沿用同一个 DSH_HOME（测"下载完 → 重启 → 应用"这种跨会话链路）
    if (-not $Reuse) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    $home_ = Join-Path $dir 'home'; $wv2 = Join-Path $dir 'wv2'
    $shots = Join-Path $VerifyRoot 'shots'
    # -Force：目录已存在时 New-Item 不报错（shots 目录跨场景复用，第二次跑必撞）
    New-Item -ItemType Directory -Force -Path $home_, $wv2, (Join-Path $home_ 'dsh-launcher'), `
        $shots | Out-Null
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start(); $port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
    if ($SafeModeSticky) {
        Set-Content -Path (Join-Path $home_ 'dsh-launcher\safe-mode.json') `
            -Value '{"active":true,"tier":1}' -Encoding utf8
    }
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $script:ExePath; $psi.WorkingDirectory = Split-Path $script:ExePath
    $psi.UseShellExecute = $false
    if ($Arguments) { $psi.Arguments = $Arguments }
    foreach ($k in 'DSH_WEB_URL','DSH_HOME','DSH_SHELL','DSH_SESSION_ID','DSH_SESSION_JSONL',
                   'DSH_TEST_NOTICE_CARD','DSH_TEST_UPDATE_SIGNAL','DSH_TEST_TOAST',
                   'DSH_TEST_INSTALL_MODE','DSH_TEST_SPLASH_DELAY_MS','DSH_TEST_CRASH',
                   'DSH_TEST_SAFE_MODE_ANSWER','DSH_TEST_MODE','DSH_TEST_INSTANCE') {
        $psi.EnvironmentVariables.Remove($k) | Out-Null }
    $psi.EnvironmentVariables['DSH_SANDBOX'] = '1'
    $psi.EnvironmentVariables['DSH_HOME'] = $home_
    $psi.EnvironmentVariables['DSH_WEB_PORT'] = "$port"
    $psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = $wv2
    $psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
    $psi.EnvironmentVariables['DSH_TELEMETRY_DISABLED'] = '1'
    $psi.EnvironmentVariables['DSH_E2E'] = '1'
    $psi.EnvironmentVariables['DSH_TEST_INSTALL_MODE'] = 'msi'   # 卡片通道（便携分支是模态决策框）
    if ($ExternalService) { $psi.EnvironmentVariables['DSH_WEB_URL'] = "http://127.0.0.1:$port" }
    if ($SplashDelayMs -gt 0) { $psi.EnvironmentVariables['DSH_TEST_SPLASH_DELAY_MS'] = "$SplashDelayMs" }
    if ($ExtraEnv) { foreach ($k in $ExtraEnv.Keys) { $psi.EnvironmentVariables[$k] = [string]$ExtraEnv[$k] } }
    return @{
        Name = $Name; Psi = $psi; Home = $home_; Port = $port
        Log = Join-Path $home_ 'dsh-launcher\dsh.log'
        Shots = Join-Path $VerifyRoot 'shots'; Dir = $dir
    }
}

function Q-Start { param($Ctx)
    $p = [System.Diagnostics.Process]::Start($Ctx.Psi)
    $Ctx['Proc'] = $p
    Write-Host ("  pid={0} port={1} home={2}" -f $p.Id, $Ctx.Port, $Ctx.Home)
    return $p
}

function Q-Log { param($Ctx)
    if (-not (Test-Path $Ctx.Log)) { return '' }
    try { return (Get-Content $Ctx.Log -Raw -ErrorAction Stop) } catch { return '' }
}

# ASCII needles only: Logger escapes non-ASCII in the JSON message body.
function Q-WaitLog { param($Ctx, [string[]]$Needles, [int]$BudgetSeconds)
    $deadline = (Get-Date).AddSeconds($BudgetSeconds)
    while ((Get-Date) -lt $deadline) {
        $t = Q-Log $Ctx
        foreach ($n in $Needles) { if ($t -match [regex]::Escape($n)) { return @{ hit = $n; text = $t } } }
        Start-Sleep -Milliseconds 500
    }
    return @{ hit = $null; text = (Q-Log $Ctx) }
}

# 同一场景重启会**共用同一个 dsh.log**：整文件扫描会让第二次启动"继承"第一次的 HEALTHY，
# 于是断言假绿、脚本提前把刚拉起的服务杀掉（实测踩过）。带 pid 的版本只看本进程的日志行。
function Q-WaitLogPid { param($Ctx, [string[]]$Needles, [int]$BudgetSeconds)
    $tag = '"pid":' + $Ctx.Proc.Id + ','
    $deadline = (Get-Date).AddSeconds($BudgetSeconds)
    while ((Get-Date) -lt $deadline) {
        $lines = @((Q-Log $Ctx) -split "`n" | Where-Object { $_ -match [regex]::Escape($tag) })
        foreach ($n in $Needles) {
            $hit = @($lines | Where-Object { $_ -match [regex]::Escape($n) })
            if ($hit.Count -gt 0) { return @{ hit = $n; text = ($hit -join "`n"); line = $hit[$hit.Count-1] } }
        }
        Start-Sleep -Milliseconds 500
    }
    return @{ hit = $null; text = ''; line = '' }
}

function Q-Lines { param($Ctx, [string]$Pattern, [int]$Last = 14)
    (Q-Log $Ctx) -split "`n" | Where-Object { $_ -match $Pattern } | Select-Object -Last $Last |
        ForEach-Object { Write-Host ("    | " + $_.Trim()) }
}

function Q-FindWindows { param($Ctx, [string]$Title = '', [int]$Pid_ = 0, [switch]$Any)
    # 主窗标题会被页面的 DocumentTitle 覆盖（实测 "DeepSeek Harness" 精确匹配取不到），
    # 故 Title 以 * 结尾时按前缀匹配；-Any = 不看标题。
    #
    # 累加器与过滤条件一律放 **script 作用域**：EnumWindows 的委托回调看不到函数局部变量
    # （实测 $pidFilter/$Title 静默变 $null，过滤器退化成"只匹配空标题窗口"，主窗直接查不到）。
    $script:QAcc = New-Object System.Collections.ArrayList
    $script:QPid = if ($Pid_ -gt 0) { $Pid_ } else { $Ctx.Proc.Id }
    $script:QAny = [bool]$Any
    $script:QPrefix = $Title.EndsWith('*')
    $script:QWant = if ($script:QPrefix) { $Title.TrimEnd('*') } else { $Title }
    $script:QEmpty = ($Title -eq '')
    $cb = [QWin+EnumProc] { param($h, $l)
        if ([QWin]::IsWindowVisible($h)) {
            $p = 0; [void][QWin]::GetWindowThreadProcessId($h, [ref]$p)
            if ($p -eq $script:QPid) {
                $sb = New-Object System.Text.StringBuilder 256
                [void][QWin]::GetWindowText($h, $sb, $sb.Capacity)
                $t = $sb.ToString()
                $hit = if ($script:QAny -or $script:QEmpty) { $true }
                       elseif ($script:QPrefix) { $t.StartsWith($script:QWant) }
                       else { $t -eq $script:QWant }
                if ($hit) {
                    $r = New-Object QWin+R
                    if ([QWin]::GetWindowRect($h, [ref]$r)) {
                        [void]$script:QAcc.Add([pscustomobject]@{ H = $h; Title = $t
                           ; X = $r.L; Y = $r.T; W = $r.Rt - $r.L; Ht = $r.B - $r.T
                           ; Dpi = [QWin]::GetDpiForWindow($h) }) } } } }
        return $true }
    [void][QWin]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:QAcc
}

# 物理坐标系的显示器拓扑（EnumDisplayMonitors → rcWork 为物理像素，GetDpiForMonitor(0)=EFFECTIVE）。
# 为什么不用 Screen.*：WinForms 的 Screen 数组在本进程里是 **逻辑**（96-DPI 基准）坐标，
# 实测 2560x1600@175% 的副屏报成 1463x914 —— 拿它当拖动目标会算错落点。
function Q-Monitors {
    $script:QMonAcc = New-Object System.Collections.ArrayList
    $script:QMonCb = [QMon+MonProc] { param($h, $dc, $r, $d)
        $mi = New-Object QMon+Info
        $mi.szDevice = ''
        $mi.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($mi)
        if ([QMon]::GetMonitorInfo($h, [ref]$mi)) {
            $dx = 0u; $dy = 0u
            [void][QMon]::GetDpiForMonitor($h, 0, [ref]$dx, [ref]$dy)
            [void]$script:QMonAcc.Add([pscustomobject]@{
                Device = $mi.szDevice; Primary = (($mi.dwFlags -band 1) -ne 0)
                X = $mi.rcWork.L; Y = $mi.rcWork.T
                W = $mi.rcWork.Rt - $mi.rcWork.L; H = $mi.rcWork.B - $mi.rcWork.T
                FullX = $mi.rcMonitor.L; FullY = $mi.rcMonitor.T
                FullW = $mi.rcMonitor.Rt - $mi.rcMonitor.L; FullH = $mi.rcMonitor.B - $mi.rcMonitor.T
                Dpi = [int]$dx }) }
        return $true }
    [void][QMon]::EnumDisplayMonitors([IntPtr]::Zero, [IntPtr]::Zero, $script:QMonCb, [IntPtr]::Zero)
    $m = $script:QMonAcc
    foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
        $hit = $m | Where-Object { $_.Device -eq $s.DeviceName }
        if ($hit) { $hit | Add-Member -NotePropertyName LogicalBounds -NotePropertyValue "$($s.Bounds.X),$($s.Bounds.Y) $($s.Bounds.Width)x$($s.Bounds.Height)" -Force }
    }
    return $m
}

function Q-PhysCursor {
    $p = New-Object QMon+Pt
    [void][QMon]::GetCursorPos([ref]$p)
    return "$($p.X),$($p.Y)"
}

function Q-WaitWindow { param($Ctx, [string]$Title, [int]$BudgetSeconds)
    $deadline = (Get-Date).AddSeconds($BudgetSeconds)
    while ((Get-Date) -lt $deadline) {
        $w = @(Q-FindWindows $Ctx $Title)
        if ($w.Count -gt 0) { return $w[0] }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Q-Click { param([int]$X, [int]$Y, [string]$What = '')
    [void][QWin]::SetCursorPos($X, $Y); Start-Sleep -Milliseconds 350
    [QWin]::mouse_event([QWin]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    [QWin]::mouse_event([QWin]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Write-Host "  click ($X,$Y) $What"
}

function Q-Hover { param([int]$X, [int]$Y, [string]$What = '')
    [void][QWin]::SetCursorPos($X, $Y); Start-Sleep -Milliseconds 300
    Write-Host "  hover ($X,$Y) $What"
}

# 自绘标题栏是客户区里的一个子窗口（CustomTitleBar : Panel），顶层窗口对它的点只报 HTCLIENT，
# 所以不能靠 y+16 猜落点——把子窗口枚举出来量它自己的矩形，顺带得到"跨屏后标题栏多高"这个实测值。
function Q-ChildWindows { param([IntPtr]$Parent)
    $script:QChildAcc = New-Object System.Collections.ArrayList
    $script:QChildParent = $Parent
    $cb = [QWin+EnumProc] { param($h, $l)
        $r = New-Object QWin+R
        if ([QWin]::GetWindowRect($h, [ref]$r)) {
            [void]$script:QChildAcc.Add([pscustomobject]@{ H = $h; X = $r.L; Y = $r.T
                ; W = $r.Rt - $r.L; Ht = $r.B - $r.T
                ; Dpi = [QWin]::GetDpiForWindow($h) }) }
        return $true }
    [void][QWin]::EnumChildWindows($Parent, $cb, [IntPtr]::Zero)
    return $script:QChildAcc
}

function Q-TitleBar { param($W)
    $kids = @(Q-ChildWindows $W.H) | Where-Object {
        $_.H -ne $W.H -and $_.W -ge ($W.W * 0.9) -and $_.Ht -ge 20 -and $_.Ht -le 250 }
    return @($kids | Sort-Object -Property Y | Select-Object -First 1)
}

# 用真实鼠标双击标题栏把窗口最大化，返回 (是否 IsZoomed, 尝试次数)。
# IsZoomed 而不是几何：几何不匹配可能是"最大化了但尺寸错"（壳的 bug），
# IsZoomed=false 才是"根本没最大化"（点击没落上/首击被激活吞掉）。
function Q-MaximizeByCaption { param($Ctx, $W, [int]$Tries = 3)
    $w = $W
    for ($i = 1; $i -le $Tries; $i++) {
        $tb = @(Q-TitleBar $w)
        if (-not $tb) {
            $kids = @(Q-ChildWindows $w.H)
            Write-Host ("  no title-bar child found (try {0}); children: {1}" -f $i,
                (($kids | ForEach-Object { "$($_.W)x$($_.Ht)@$($_.X),$($_.Y)" }) -join ' | '))
            return @{ zoomed = $false; tries = $i; w = $w; caption = 'not-found' }
        }
        # 水平中心：避开左侧版本徽标与右侧三键；垂直中心避开上下边缘
        $x = [int]($tb[0].X + $tb[0].W / 2); $y = [int]($tb[0].Y + $tb[0].Ht / 2)
        Write-Host ("  title bar child {0}x{1} at ({2},{3}) dpi={4} -> click ({5},{6})" -f `
            $tb[0].W, $tb[0].Ht, $tb[0].X, $tb[0].Y, $tb[0].Dpi, $x, $y)
        [void][QWin]::SetForegroundWindow($w.H); Start-Sleep -Milliseconds 400
        Q-Click $x $y "activate (#$i)"
        Start-Sleep -Milliseconds 300
        Q-DoubleClick $x $y "caption -> maximize (#$i)"
        $until = (Get-Date).AddSeconds(6)
        while ((Get-Date) -lt $until) {
            if ([QWin]::IsZoomed($w.H)) { return @{ zoomed = $true; tries = $i; w = $w; caption = "$($tb[0].W)x$($tb[0].Ht)" } }
            Start-Sleep -Milliseconds 300
        }
        $r = New-Object QWin+R
        [void][QWin]::GetWindowRect($w.H, [ref]$r)
        Write-Host ("  still not zoomed after try {0}: {1}x{2} at ({3},{4}) iconic={5}" -f `
            $i, ($r.Rt - $r.L), ($r.B - $r.T), $r.L, $r.T, [QWin]::IsIconic($w.H))
        $fresh = @(Q-FindWindows $Ctx '') | Where-Object { $_.H -eq $w.H } | Select-Object -First 1
        if (-not $fresh) { return @{ zoomed = $false; tries = $i; w = $null; caption = "$($tb[0].W)x$($tb[0].Ht)" } }
        $w = $fresh
    }
    return @{ zoomed = $false; tries = $Tries; w = $w; caption = 'exhausted' }
}

# 真实拖动窗口：先激活并**确认真的到了前台**，再按住拖；拖完核对窗口确实动了位置，没动就重试。
# 为什么不能像以前那样"按下即拖"：自绘标题栏改成拖拽阈值后（T12 修复），第一下点击若被
# WM_MOUSEACTIVATE 吃掉，就不会有第二次 MouseMove 越阈值 → 窗口纹丝不动（真机实测：同一手势
# 连跑 4 轮，1 轮完全没拖起来）。旧实现靠 MouseDown 里无条件 SendMessage 侥幸免疫这一点。
function Q-DragWindow { param($Ctx, $W, [int]$ToX, [int]$ToY, [int]$Tries = 3, [string]$What = '')
    $w = $W
    for ($i = 1; $i -le $Tries; $i++) {
        $tb = @(Q-TitleBar $w)
        if ($tb) { $gx = [int]($tb[0].X + $tb[0].W/2); $gy = [int]($tb[0].Y + $tb[0].Ht/2) }
        else { $gx = $w.X + 200; $gy = $w.Y + 16 }
        [void][QWin]::SetForegroundWindow($w.H)
        $fg = (Get-Date).AddSeconds(3)
        while ((Get-Date) -lt $fg -and [QWin]::GetForegroundWindow() -ne $w.H) {
            Start-Sleep -Milliseconds 200
        }
        Write-Host ("  drag #{0}: grab ({1},{2}) -> ({3},{4}) foreground={5} {6}" -f `
            $i, $gx, $gy, ($gx + ($ToX - $w.X)), ($gy + ($ToY - $w.Y)),
            ([QWin]::GetForegroundWindow() -eq $w.H), $What)
        Q-DragTo $gx $gy ($gx + ($ToX - $w.X)) ($gy + ($ToY - $w.Y)) $What
        Start-Sleep -Seconds 2
        $after = @(Q-FindWindows $Ctx '') | Where-Object { $_.H -eq $w.H } | Select-Object -First 1
        if (-not $after) { return $null }
        if ($after.X -ne $w.X -or $after.Y -ne $w.Y) { return $after }
        Write-Host "  window did not move; retrying after activation"
        $w = $after
    }
    return $w
}

# 真实拖动：按住不放、分步移动、再松开（跨屏移动只有这条路能触发 WM_DPICHANGED / 逐屏定位）
function Q-DragTo { param([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY, [string]$What = '')
    [void][QWin]::SetCursorPos($FromX, $FromY); Start-Sleep -Milliseconds 300
    [QWin]::mouse_event([QWin]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    $steps = 18
    for ($i = 1; $i -le $steps; $i++) {
        $x = [int]($FromX + ($ToX - $FromX) * $i / $steps)
        $y = [int]($FromY + ($ToY - $FromY) * $i / $steps)
        [void][QWin]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 45
    }
    [QWin]::mouse_event([QWin]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Write-Host "  drag ($FromX,$FromY)->($ToX,$ToY) $What"
}

function Q-DoubleClick { param([int]$X, [int]$Y, [string]$What = '')
    [void][QWin]::SetCursorPos($X, $Y); Start-Sleep -Milliseconds 250
    [QWin]::mouse_event([QWin]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero); [QWin]::mouse_event([QWin]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [QWin]::mouse_event([QWin]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero); [QWin]::mouse_event([QWin]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Write-Host "  double-click ($X,$Y) $What"
}

# 模态 MessageBox 的按钮位置随主题/DPI 变动，点坐标不稳；默认按钮（是/OK）用键盘确认，
# 且先 SetForegroundWindow 到该对话框——否则按键会落到别的前台窗口上（用户当时正在用别的程序）。
function Q-PressDefault { param($Hwnd)
    if ($Hwnd) { [void][QWin]::SetForegroundWindow($Hwnd); Start-Sleep -Milliseconds 500 }
    [QWin]::keybd_event(0x0D, 0, 0, [IntPtr]::Zero)          # VK_RETURN down
    [QWin]::keybd_event(0x0D, 0, 2, [IntPtr]::Zero)          # up
    Write-Host "  Enter (default button) hwnd=$Hwnd fg=$([QWin]::GetForegroundWindow())"
}

# 等模态对话框 → 截图留证 → 确认默认按钮；2 秒后仍在就按控件矩形真点（GetDlgItem 兜底）。
function Q-ConfirmDialog { param($Ctx, [string]$Title, [int]$BudgetSeconds = 20, [string]$ShotName = '')
    $d = Q-WaitWindow $Ctx $Title $BudgetSeconds
    if (-not $d) { Write-Host "  no dialog [$Title] within ${BudgetSeconds}s"; return $null }
    Write-Host ("  dialog [{0}] {1}x{2} at ({3},{4})" -f $d.Title, $d.W, $d.Ht, $d.X, $d.Y)
    if ($ShotName) { Q-Shot $Ctx $ShotName | Out-Null }
    Q-PressDefault $d.H
    Start-Sleep -Seconds 2
    if (@(Q-FindWindows $Ctx $Title).Count -gt 0) {
        Write-Host "  dialog survived Enter -> real click on IDYES rect (with retry + BM_CLICK fallback)"
        & (Join-Path $script:VerifyRoot 'click-dlg.ps1') -TitleContains $Title -Id 6
    }
    $answered = (@(Q-FindWindows $Ctx $Title).Count -eq 0)
    Write-Host ("  dialog answered = {0}" -f $answered)
    return [pscustomobject]@{ H = $d.H; Title = $d.Title; Answered = $answered }
}

function Q-WpnApps { param($Ctx)
    # 只扫目标 PID（全机枚举极慢，且会把别人的进程算进来）
    try {
        $p = [System.Diagnostics.Process]::GetProcessById($Ctx.Proc.Id)
        $hits = @($p.Modules | Where-Object { $_.ModuleName -match 'wpnapps' } | ForEach-Object { $_.ModuleName })
        return @{ alive = $true; hits = $hits }
    } catch { return @{ alive = $false; hits = @() } }
}

function Q-Stop { param($Ctx)
    if (-not $Ctx.Proc) { return }
    $sysRoot = if ($env:SystemRoot) { $env:SystemRoot } else { 'C:\Windows' }
    try { & (Join-Path $sysRoot 'System32\taskkill.exe') /T /F /PID $Ctx.Proc.Id 2>&1 | Out-Null }
    catch { Write-Host "  (taskkill said: $($_.Exception.Message))" }
    Start-Sleep -Seconds 2
    $left = @(Get-Process -Id $Ctx.Proc.Id -ErrorAction SilentlyContinue).Count
    Write-Host ("  cleanup taskkill /T /F pid={0} left={1}" -f $Ctx.Proc.Id, $left)
    # 服务子进程可能不在这棵树里（被接管/自重启）：按端口反查后按 PID 精确清
    try {
        $conns = Get-NetTCPConnection -LocalPort $Ctx.Port -State Listen -ErrorAction SilentlyContinue
        foreach ($c in $conns) {
            if ($c.OwningProcess -ne $Ctx.Proc.Id) {
                & (Join-Path $sysRoot 'System32\taskkill.exe') /T /F /PID $c.OwningProcess 2>&1 | Out-Null
                Write-Host ("  cleanup listener pid={0} on port {1}" -f $c.OwningProcess, $Ctx.Port) } }
    } catch { Write-Host "  (port cleanup skipped: $($_.Exception.Message))" }
}

function Q-Result { param([string]$Id, [string]$Verdict, [string]$Evidence)
    $color = 'Red'
    if ($Verdict -eq 'PASS') { $color = 'Green' }
    if ($Verdict -eq 'SKIP') { $color = 'Yellow' }
    Write-Host ("[{0}] {1} :: {2}" -f $Verdict, $Id, $Evidence) -ForegroundColor $color
    $script:Results += [pscustomobject]@{ Id = $Id; Verdict = $Verdict; Evidence = $Evidence }
}

# 假 dsh 服务：只用来让"页面 → 壳"的真实消息通路跑起来（T4 的插件崩溃消息）。
function Q-StartFakeServer {
    param([int]$Port, [string]$HtmlPath)
    $reqLog = [string](Join-Path (Split-Path -Parent $HtmlPath) ("fake-req-$Port.log"))
    $job = Start-Job -ScriptBlock {
        param($p, $bodyPath, $reqLog)
        $html = [IO.File]::ReadAllBytes($bodyPath)
        $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $p); $l.Start()
        while ($true) {
            try {
                $c = $l.AcceptTcpClient(); $s = $c.GetStream()
                $b = New-Object byte[] 8192
                $n = $s.Read($b, 0, $b.Length)
                if ($n -gt 0) {
                    $head = [Text.Encoding]::ASCII.GetString($b, 0, $n) -split "`r`n"
                    Add-Content -Path $reqLog -Value $head[0].Trim()
                }
                $h = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($html.Length)`r`nConnection: close`r`n`r`n"
                $s.Write([Text.Encoding]::ASCII.GetBytes($h)); $s.Write($html); $c.Close()
            } catch { Start-Sleep -Milliseconds 100 }
        }
    } -ArgumentList $Port, $HtmlPath, $reqLog
    return @{ Job = $job; ReqLog = $reqLog }
}

function Q-StopFakeServer { param($Srv)
    if ($Srv) { Stop-Job $Srv.Job -ErrorAction SilentlyContinue; Remove-Job $Srv.Job -Force -ErrorAction SilentlyContinue }
}

# 壳内建的生产测试钩子（DSH_TEST_MODE=1 起 NamedPipe）：拿"生产自己算出来的"徽标矩形，
# 比按像素猜点击位置可靠。返回解析后的 JSON 对象。
function Q-Pipe { param([int]$ProcId, [string]$Request, [int]$BudgetSeconds = 20)
    $deadline = (Get-Date).AddSeconds($BudgetSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "dsh-launcher-uites-$ProcId",
                [System.IO.Pipes.PipeDirection]::InOut)
            $c.Connect(2000)
            $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
            $w.WriteLine($Request)
            $r = New-Object System.IO.StreamReader($c)
            $line = $r.ReadLine()
            $c.Dispose()
            if ($line) { return ($line | ConvertFrom-Json) }
        } catch { Start-Sleep -Milliseconds 400 }
    }
    return $null
}
$script:Results = @()
function Q-Summary {
    Write-Host "`n==== summary ===="
    $script:Results | ForEach-Object { Write-Host ("{0,-6} {1,-6} {2}" -f $_.Verdict, $_.Id, $_.Evidence) }
}
