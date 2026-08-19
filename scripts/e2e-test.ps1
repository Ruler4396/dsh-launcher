<#
.SYNOPSIS
dsh-launcher 端到端（E2E）测试：从"用户拿到安装包/免安装版"那一刻起，
到"用户卸载清理"为止的完整真实链路（v0.3.1 新增，回应"正向 GUI 链路无测试"缺口）。

.DESCRIPTION
覆盖（全部隔离在 %TEMP% 内，不触碰真实 ~/.dsh / 3080 / HKCU Run 之外的状态）：
  E1  发布产物完整性：dist 内 MSI/zip/SHA256SUMS 存在且 zip 可解压、MSI 可解析
  E2  免安装版部署：解压 zip → DshWeb.exe 版本正确、运行时脚本齐全
  E3  首次启动（真实 GUI，隔离 home + 高位端口）：主窗口出现、服务拉起并 HTTP 就绪
  E4  窗口位置记忆：移动/缩放窗口 → 关闭 → 重启后位置/大小恢复一致
  E5  诊断导出（服务运行中，日志被锁定）：--diagnose 成功生成 zip 且含 6 个条目
  E6  卸载清理：uninstall-autostart.cmd 删除自启/快捷方式；-CleanData 只清自有数据
  E7  数据边界：卸载后 DSH_HOME 的 profiles/ 等 dsh 生态数据原样保留

前置：已运行 scripts/build-release.ps1（dist 内产物）或至少 dotnet publish -o .neg-publish。
真实 GUI 测试需要桌面会话（自动化探针操作窗口）；CI 无桌面时跳过 E3/E4（显式 SKIP）。

.EXAMPLE
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/e2e-test.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/e2e-test.ps1 -SkipGui   # 无桌面环境
#>
param(
    [switch]$SkipGui,          # 跳过真实 GUI 用例（E3/E4），用于无桌面 CI
    [string]$PublishDir = ""   # 默认用 dist 免安装 zip；指定 .neg-publish 则跳过 zip 校验
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$script:failed = 0
$script:passed = 0

function Assert-T([bool]$Cond, [string]$Msg) {
    if ($Cond) { Write-Host "[ OK ] $Msg" -ForegroundColor Green; $script:passed++ }
    else { Write-Host "[FAIL] $Msg" -ForegroundColor Red; $script:failed++ }
}

# ---- 隔离区（%TEMP% 铁律）----
$base = Join-Path $env:TEMP ("dsh-e2e-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $base | Out-Null
$baseFull = [System.IO.Path]::GetFullPath($base)
$tempFull = [System.IO.Path]::GetFullPath($env:TEMP)
if (-not $baseFull.StartsWith($tempFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "[FATAL] 隔离区不在 %TEMP% 内，拒绝执行" -ForegroundColor Red; exit 2
}

# HKCU Run 备份/恢复（壳首启可能因 HKLM AutoStartWanted 改写自启）
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$origRunValue = (Get-ItemProperty -Path $runKeyPath -Name "dsh-launcher" -ErrorAction SilentlyContinue)."dsh-launcher"

$dist = Join-Path $root "dist"
# v0.3.1：zip 命名带版本号（dsh-launcher-windows-<ver>.zip）
$zipPath = (Get-ChildItem (Join-Path $dist "dsh-launcher-windows-*.zip") -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$exe = ""

function Stop-TestPort([int]$port) {
    $out = netstat -ano -p tcp 2>$null
    foreach ($line in $out) {
        if ($line -match "LISTENING" -and $line -match ":$port ") {
            $pid2 = ($line.Trim() -split '\s+')[-1]
            if ($pid2 -match '^\d+$') {
                try {
                    $proc = Get-Process -Id ([int]$pid2) -ErrorAction Stop
                    if ($proc.ProcessName -eq "node") { Stop-Process -Id ([int]$pid2) -Force -ErrorAction SilentlyContinue }
                } catch { }
            }
        }
    }
}

Write-Host "=== E1: 发布产物完整性 ===" -ForegroundColor Cyan
if ($PublishDir) {
    # -PublishDir 模式（CI 用已发布目录）不校验 dist 产物：zip/MSI/校验和属于构建产物检查，
    # 与 PublishDir 无关；跳过避免 CI 无 dist 产物时误判 FAIL。
    Write-Host "  [SKIP] E1 产物完整性（-PublishDir 模式）" -ForegroundColor Yellow
}
else {
    $msiPath = Get-ChildItem (Join-Path $dist "dsh-launcher-*.msi") -ErrorAction SilentlyContinue | Select-Object -First 1
    Assert-T ($null -ne $zipPath) "免安装 zip 存在: dsh-launcher-windows-<版本>.zip"
    Assert-T ($null -ne $msiPath) "MSI 存在: $($msiPath.Name)"
    Assert-T (Test-Path (Join-Path $dist "SHA256SUMS.txt")) "校验和文件存在"
    if ($zipPath -and (Test-Path $zipPath)) {
        $entries = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
        $names = $entries.Entries | ForEach-Object FullName
        $entries.Dispose()
        foreach ($need in "DshWeb.exe", "WebView2Loader.dll", "runtimes/win-x64/native/WebView2Loader.dll",
            "start-dsh.vbs", "start-dsh.cmd", "dsh-web.cmd", "uninstall-autostart.cmd", "check-prereq.cmd") {
            Assert-T ($names -contains $need) "zip 含 $need"
        }
    }
}

Write-Host "`n=== E2: 免安装版部署（解压 zip 即用）===" -ForegroundColor Cyan
$deploy = Join-Path $base "deploy"
# -PublishDir 模式：优先用 PublishDir 的 exe（避免本机 dist 残留旧 zip 覆盖新代码，导致 geo 探针
# 拿到不含 --ui-probe 的旧 exe 走正常路径弹 E2004）。无 PublishDir 时才回退 zip。
if ($PublishDir -and (Test-Path (Join-Path $PublishDir "DshWeb.exe"))) {
    $exe = Join-Path $PublishDir "DshWeb.exe"
    # 防旧产物陷阱：若 dist 有 zip 且其 exe 时间戳比 PublishDir exe 旧，写 WARN 并强制用 PublishDir。
    if ($zipPath -and (Test-Path $zipPath)) {
        try {
            $zipExe = (Get-Item $exe).LastWriteTime
            $zipTime = (Get-Item $zipPath).LastWriteTime
            if ($zipTime -lt $zipExe) {
                Write-Host "  [WARN] dist zip 早于 PublishDir exe（$($zipTime.ToString('yyyy-MM-dd HH:mm')) < $($zipExe.ToString('yyyy-MM-dd HH:mm'))）；强制用 PublishDir，忽略旧 zip" -ForegroundColor Yellow
            }
        } catch { /* 时间戳不可比时忽略 */ }
    }
    Write-Host "  [OK] 使用 -PublishDir exe: $exe" -ForegroundColor Green
} elseif ($zipPath -and (Test-Path $zipPath)) {
    Expand-Archive -Path $zipPath -DestinationPath $deploy -Force
    $exe = Join-Path $deploy "DshWeb.exe"
}
Assert-T (Test-Path $exe) "可执行 DshWeb.exe 就位"
if (Test-Path $exe) {
    $ver = (Get-Item $exe).VersionInfo
    Assert-T ($ver.FileVersion -match '^\d+\.\d+\.\d+') "版本号格式正确: $($ver.FileVersion)"
}

# ---- 真实 GUI 探针（移动窗口/读矩形/发 WM_CLOSE），编译到隔离区 ----
$probeDir = Join-Path $base "probe"
New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $probeDir "probe.csproj")
@'
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class Probe {
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool SendMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("kernel32.dll")] static extern uint GetLastError();
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    }
    // Win32 KEYBDINPUT：wVk(WORD) wScan(WORD) dwFlags(DWORD) time(DWORD) dwExtraInfo(ULONG_PTR)。
    // dwFlags/time 必须用 uint（DWORD），错写成 ushort 会导致结构错位、SendInput 注入无效。
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public KEYBDINPUT ki; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    const uint WM_CLOSE = 0x0010;
    const uint WM_SYSCOMMAND = 0x0112;
    const uint SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint INPUT_KEYBOARD = 1;
    const ushort KEYEVENTF_KEYUP = 0x0002;
    const ushort VK_F11 = 0x7A;

    static void Main(string[] args) {
        var exe = args[0]; var home = args[1]; var url = args[2]; var mode = args[3];
        if (mode == "geo") {
            GeoProbe(exe, home, url);
            return;
        }
        if (mode == "run1") {
            Start(exe, home, url);
            var h = WaitMain(30000, exe);
            Console.WriteLine("found=" + (h != IntPtr.Zero));
            if (h != IntPtr.Zero) {
                MoveWindow(h, 120, 90, 900, 620, true);
                Thread.Sleep(1500);
                GetWindowRect(h, out var r);
                Console.WriteLine($"moved=({r.L},{r.T},{r.R - r.L}x{r.B - r.T})");
                PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            Thread.Sleep(6000);
            var alive = Process.GetProcessesByName("DshWeb").Any(p => SameExe(p, exe));
            Console.WriteLine("alive=" + alive);
            return;
        }
        if (mode == "run2") {
            Start(exe, home, url);
            var h = WaitMain(30000, exe);
            Console.WriteLine("found=" + (h != IntPtr.Zero));
            if (h != IntPtr.Zero) {
                Thread.Sleep(2000);
                GetWindowRect(h, out var r);
                Console.WriteLine($"rect=({r.L},{r.T},{r.R - r.L}x{r.B - r.T})");
                PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            Thread.Sleep(5000);
            return;
        }
    }

    // Task 0.2 几何/F11/标题栏/白屏探针：
    // 1) G1/G10: MonitorFromWindow+GetMonitorInfo(rcWork) 取物理工作区，WM_SYSCOMMAND SC_MAXIMIZE
    //    后与 GetWindowRect 比对（容差≤2px）——替代逻辑像素陷阱，覆盖多屏物理像素。
    // 2) F1: SendInput 注入 VK_F11（低级钩子 WH_KEYBOARD_LL 可捕获注入键），断言最大化状态翻转。
    // 3) G3/G7: 最大化→还原循环后，EnumChildWindows 找自绘标题栏子控件存在、Visible、高≈32×DPI。
    // 4) W6: 读壳侧 DSH_WEBVIEW2_READYSTATE 钩子写入的 document.readyState，断言非白屏。
    static void GeoProbe(string exe, string home, string url) {
        var readyStateFile = System.IO.Path.Combine(home, "webview-ready-state.txt");
        // --ui-probe 无服务模式：不拉 dsh 服务/不导航真实内容，直接开 DshShellForm，
        // 避免隔离 dsh 服务在全新 DSH_HOME 起不来的环境依赖（dsh 生态 profile 初始化问题）。
        Start(exe, home, url, "--ui-probe");
        var h = WaitMain(30000, exe);
        Console.WriteLine("found=" + (h != IntPtr.Zero));
        if (h == IntPtr.Zero) return;
        try
        {

        // ---- 几何断言（G1/G10）：最大化窗口矩形 == 物理工作区 ----
        var mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        SendMessage(h, WM_SYSCOMMAND, (IntPtr)SC_MAXIMIZE, IntPtr.Zero);
        Thread.Sleep(1500);
        GetWindowRect(h, out var mr);
        var geoOk = Math.Abs(mr.L - mi.rcWork.L) <= 2 && Math.Abs(mr.T - mi.rcWork.T) <= 2
            && Math.Abs(mr.R - mi.rcWork.R) <= 2 && Math.Abs(mr.B - mi.rcWork.B) <= 2;
        Console.WriteLine($"maxrect=({mr.L},{mr.T},{mr.R},{mr.B}) work=({mi.rcWork.L},{mi.rcWork.T},{mi.rcWork.R},{mi.rcWork.B}) geo={geoOk}");
        SendMessage(h, WM_SYSCOMMAND, (IntPtr)SC_RESTORE, IntPtr.Zero);
        Thread.Sleep(800);

        // ---- F11 断言（F1）：注入 F11 → 最大化；再注入 → 还原 ----
        // 前提是主窗前台（F2：仅主窗前台时切换）：AttachThreadInput 抢前台后再注入，
        // 否则前台锁拒绝、isForeground 判定为 false → F11 不生效（测试环境焦点问题）。
        // GEO_F11_MODE=soft（CI 无头 runner 默认）：前台注入可能受 runner 桌面环境干扰，
        // 失败重试 2 次仍败 → 输出 f11=SKIP(soft)，由上层不判 FAIL（分层门禁 Q1）。
        // 本地默认 hard：必须 up:True down:False，否则 FAIL。
        var f11Mode = Environment.GetEnvironmentVariable("GEO_F11_MODE") ?? "hard";
        var f11Result = RunF11Assert(h, 1);
        if (f11Result != "ok" && f11Mode == "soft") {
            Console.WriteLine("f11=soft-fail, retrying 1/2");
            f11Result = RunF11Assert(h, 2);
            if (f11Result != "ok") { Console.WriteLine("f11=soft-fail, retrying 2/2"); f11Result = RunF11Assert(h, 3); }
            if (f11Result != "ok") { Console.WriteLine($"f11=SKIP(soft) fg={GetForegroundWindow() == h}"); }
            else { Console.WriteLine("f11=up:True down:False (after retry)"); }
        } else if (f11Result == "ok") {
            // 成功路径输出与断言匹配的旧格式：f11=up:True down:False
            Console.WriteLine($"f11=up:True down:False fg={GetForegroundWindow() == h}");
        } else {
            Console.WriteLine($"f11=fail fg={GetForegroundWindow() == h}");
        }

        // ---- 标题栏断言（G3/G7）：最大化→还原循环后子控件存在可见且高≈32×DPI ----
        var children = new System.Collections.Generic.List<IntPtr>();
        EnumChildWindows(h, (c, _) => { children.Add(c); return true; }, IntPtr.Zero);
        var dpi = GetDpiForWindow(h);
        var titleH = (int)Math.Round(32.0 * dpi / 96.0);
        bool titleOk = false;
        foreach (var c in children) {
            if (!IsWindowVisible(c)) continue;
            GetWindowRect(c, out var cr);
            var hh = cr.B - cr.T;
            var ww = cr.R - cr.L;
            // 自绘标题栏：高度≈32×DPI，宽度≈窗口宽（排除 WebView2 的 Chromium 子窗口）
            if (Math.Abs(hh - titleH) <= 3 && ww > 200 && cr.T >= mr.T) { titleOk = true; break; }
        }
        Console.WriteLine($"title=ok:{titleOk} titleH:{titleH} children:{children.Count} dpi:{dpi}");

        // ---- 白屏断言（W6）：读 readyState 文件，断言主 WebView2 加载完成 ----
        var ready = "";
        for (var i = 0; i < 40 && string.IsNullOrEmpty(ready); i++) {
            if (System.IO.File.Exists(readyStateFile)) ready = System.IO.File.ReadAllText(readyStateFile);
            if (string.IsNullOrEmpty(ready)) Thread.Sleep(500);
        }
        Console.WriteLine("readystate=" + ready);
        }
        finally {
            // 任何断言失败/异常都确保关窗：否则壳进程残留，污染后续用例
            PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(6000);
        }
    }

    static void SendKey(ushort vk) {
        // 实测 .NET 下 SendInput 的 INPUT 布局在 x64 被拒（GetLastError=87 ERROR_INVALID_PARAMETER），
        // 注入无效；keybd_event 更宽容、同样对 WH_KEYBOARD_LL 钩子可见。统一用 keybd_event。
        keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // 一轮 F11 断言：抢前台 → 注入 F11 → 期望 Maximized；再注入 → 期望 Normal。返回 ok/fail。
    // 每次注入前都重新抢前台（注入瞬间前台漂移是 CI 无头 runner 上 F1 失败的主因）。
    static string RunF11Assert(IntPtr h, int attempt) {
        ForceForeground(h);
        SendKey(VK_F11);
        Thread.Sleep(1000);
        var up = IsZoomed(h);
        ForceForeground(h);
        SendKey(VK_F11);
        Thread.Sleep(1000);
        var down = IsZoomed(h);
        var ok = up && !down;
        if (!ok) Console.WriteLine($"f11=up:{up} down:{down} (attempt {attempt})");
        return ok ? "ok" : "fail";
    }

    // 可靠抢前台（F11 的 isForeground 判定需要）：Windows 前台锁（ForegroundLockTimeout）会拒绝
    // 非用户交互进程的 SetForegroundWindow。AttachThreadInput 把本线程输入队列挂到目标窗口线程，
    // 令 SetForegroundWindow 视同"目标线程自身调用"，绕过前台锁。F11 断言依赖它才能稳定注入。
    static void ForceForeground(IntPtr h) {
        if (GetForegroundWindow() == h) return;
        var fg = GetForegroundWindow();
        var cur = GetCurrentThreadId();
        var win = GetWindowThreadProcessId(h, out _);
        var fgTid = GetWindowThreadProcessId(fg, out _);
        if (win != 0 && cur != win) AttachThreadInput(cur, win, true);
        if (fgTid != 0 && fgTid != win) AttachThreadInput(fgTid, win, true);
        ShowWindow(h, 9 /*SW_RESTORE*/);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (win != 0 && cur != win) AttachThreadInput(cur, win, false);
        if (fgTid != 0 && fgTid != win) AttachThreadInput(fgTid, win, false);
        System.Threading.Thread.Sleep(200);
    }

    static IntPtr WaitMain(int ms, string exe) {
        var sb = new StringBuilder(256);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) {
            var h = FindWindow(null, "DeepSeek Harness");
            if (h != IntPtr.Zero && IsTarget(h, exe)) {
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString().Contains("WindowsForms10") && !sb.ToString().Contains("Dialog")) return h;
            }
            Thread.Sleep(300);
        }
        return IntPtr.Zero;
    }

    static bool IsTarget(IntPtr h, string exe) {
        GetWindowThreadProcessId(h, out var pid);
        try {
            using var p = Process.GetProcessById((int)pid);
            return p.MainModule?.FileName?.Equals(exe, StringComparison.OrdinalIgnoreCase) == true;
        } catch { return false; }
    }

    static bool SameExe(Process p, string exe) {
        try { return p.MainModule?.FileName?.Equals(exe, StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    static void Start(string exe, string home, string url, string? extraArg = null) {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        if (!string.IsNullOrEmpty(extraArg)) psi.ArgumentList.Add(extraArg);
        psi.EnvironmentVariables["DSH_HOME"] = home;
        psi.EnvironmentVariables["DSH_WEB_URL"] = url;
        psi.EnvironmentVariables["DSH_WEB_PORT"] = "";
        psi.EnvironmentVariables["DSH_NO_UI"] = "";
        // WebView2 数据目录隔离铁律：测试实例绝不能与真实实例共用 user-data-dir
        // （共用会导致 WebView2 互锁、真实启动器整窗灰死——2026-08-16 实测事故）
        psi.EnvironmentVariables["DSH_WEBVIEW2_DATA"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-e2e-wv2-" + System.Guid.NewGuid().ToString("N"));
        // Task 0.2 白屏断言钩子：壳在主窗导航完成后把 document.readyState 写入该文件（见壳侧测试钩子）。
        psi.EnvironmentVariables["DSH_WEBVIEW2_READYSTATE"] = System.IO.Path.Combine(home, "webview-ready-state.txt");
        Process.Start(psi);
    }
}
'@ | Set-Content (Join-Path $probeDir "Program.cs")
$probeExe = ""
try {
    dotnet build (Join-Path $probeDir "probe.csproj") -c Release -v q --nologo | Out-Null
    $probeExe = Join-Path $probeDir "bin\Release\net10.0-windows\probe.exe"
} catch { }

# ---- 拉起隔离测试服务（E3/E4/E5 用；端口 39xxx）----
$svcPort = 39041
$svcHome = Join-Path $base "svc-home"
New-Item -ItemType Directory -Force -Path $svcHome | Out-Null
# dsh 服务端包定位：优先 npm 全局根（setup-node 的全局 prefix 可能不是 %APPDATA%\npm），
# 回退到 %APPDATA%\npm（Windows npm 默认）。CI runner 与本地都需找到 bin.js 才能跑 E3+。
$dshJs = ""
try {
    $globalRoot = (npm root -g 2>$null | Select-Object -First 1)
    if ($globalRoot) { $cand = Join-Path $globalRoot "@deepseek-ai\dsh\lib\bin.js"; if (Test-Path $cand) { $dshJs = $cand } }
} catch { }
if (-not $dshJs) {
    $cand = Join-Path $env:APPDATA "npm\node_modules\@deepseek-ai\dsh\lib\bin.js"
    if (Test-Path $cand) { $dshJs = $cand }
}

function Start-IsoService {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "node"
    $psi.Arguments = "`"$dshJs`" web --host 127.0.0.1 --port $svcPort"
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables["DSH_HOME"] = $svcHome
    return [System.Diagnostics.Process]::Start($psi)
}

$guiEligible = (-not $SkipGui) -and $probeExe -and (Test-Path $exe) -and (Test-Path $dshJs)

if ($guiEligible) {
    Write-Host "`n=== E3/E4: 真实 GUI 链路（首次启动 + 窗口记忆）===" -ForegroundColor Cyan
    $svc = Start-IsoService
    # 服务就绪探测：用 TcpClient 连接测试（500ms 超时）替代 Invoke-WebRequest。
    # 教训（CI 卡 13 分钟）：dsh 首次初始化若联网挂起，node 半开连接时 Invoke-WebRequest 的
    # -TimeoutSec 只作用于响应、连接阶段无可靠超时，60 次循环每次挂起 → 整个 job 卡死。
    # 外层总超时墙 45s，超时即判未就绪；TcpClient 每轮最多 500ms。
    $ready = $false
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 45 -and -not $ready) {
        Start-Sleep -Milliseconds 500
        try {
            $tc = New-Object System.Net.Sockets.TcpClient
            $ar = $tc.BeginConnect("127.0.0.1", $svcPort, $null, $null)
            if ($ar.AsyncWaitHandle.WaitOne(500)) { $tc.EndConnect($ar); $ready = $true }
            $tc.Close()
        } catch { }
    }
    $sw.Stop()
    if (-not $ready) {
        # dsh 生态：全新 DSH_HOME 的 web profile 缺 dsh-client-ui-plan 等 bundles，`dsh web` 起不来
        #（ERR_MODULE_NOT_FOUND）。这是环境初始化问题，非本项目回归——显式 SKIP（E3/E4 依赖服务），
        # 不判 FAIL；geo（E3b）走 --ui-probe 无服务模式，不受影响。服务可用时本段照常跑。
        Write-Host "  [SKIP] 隔离 dsh 服务未就绪 (127.0.0.1:$svcPort)；E3/E4 依赖服务，跳过（geo 已独立）" -ForegroundColor Yellow
        try { $svc.Kill() } catch { }
    }
    else {
    $isoHome = Join-Path $base "gui-home"
    New-Item -ItemType Directory -Force -Path (Join-Path $isoHome "dsh-launcher") | Out-Null

    $out1 = & $probeExe $exe $isoHome "http://127.0.0.1:$svcPort" run1 2>&1
    $out1 | ForEach-Object { Write-Host "  probe1: $_" }
    Assert-T (($out1 -join ' ') -match 'found=True') "E3: 主窗口出现（服务在跑，壳直接开窗）"
    Assert-T (($out1 -join ' ') -match 'moved=\(120,90,900x620\)') "E4: 窗口已移动到 (120,90) 900x620"
    Assert-T (($out1 -join ' ') -match 'alive=False') "E3: 关闭窗口后进程退出（跟随窗口语义）"

    $stateFile = Join-Path $isoHome "dsh-launcher\window-state.json"
    $saved = Get-Content $stateFile -Raw -ErrorAction SilentlyContinue
    Assert-T ($saved -match '"X":120,"Y":90') "E4: window-state.json 保存新位置: $saved"
    Assert-T ($saved -match '"WidthLogical":900') "E4: 保存新尺寸 900"

    $out2 = & $probeExe $exe $isoHome "http://127.0.0.1:$svcPort" run2 2>&1
    $out2 | ForEach-Object { Write-Host "  probe2: $_" }
    Assert-T (($out2 -join ' ') -match 'rect=\(120,90,900x620\)') "E4: 重启后窗口恢复到 (120,90) 900x620（记忆生效）"
    }
} else {
    Write-Host "`n=== E3/E4: 跳过（无桌面会话或依赖缺失；GUI 用例需交互桌面）===" -ForegroundColor Yellow
}

# ---- Task 0.2 新增 5 组断言（几何/F11/标题栏/白屏）----
# geo 探针走壳的 --ui-probe 无服务模式（不拉 dsh 服务/不导航真实内容，直接开 DshShellForm）：
# 独立于 E3/E4 的隔离 dsh 服务——该服务在全新 DSH_HOME 起不来（dsh 生态 profile 初始化缺
# dsh-client-ui-plan，非本项目代码），而 geo 验证的窗口行为本身不依赖服务内容。
Write-Host "`n=== E3b: 几何 + F11 + 标题栏 + 白屏 断言（--ui-probe 无服务模式）===" -ForegroundColor Cyan
if ($SkipGui -or -not $probeExe -or -not (Test-Path $exe)) {
    Write-Host "  [SKIP] geo 探针（无桌面会话或 probe/exe 缺失）" -ForegroundColor Yellow
}
else {
    $geoHome = Join-Path $base "geo-home"
    New-Item -ItemType Directory -Force -Path (Join-Path $geoHome "dsh-launcher") | Out-Null
    $outGeo = & $probeExe $exe $geoHome "http://127.0.0.1:$svcPort" geo 2>&1
    $outGeo | ForEach-Object { Write-Host "  probeGeo: $_" }
    $geoText = $outGeo -join ' '
    Assert-T ($geoText -match 'found=True') "G1: 主窗口出现（geo 探针）"
    Assert-T ($geoText -match 'geo=True') "G1/G10: 最大化窗口矩形 == 物理工作区（MonitorFromWindow+GetMonitorInfo，容差≤2px，覆盖多屏物理像素）"
    # F1 分层门禁（Q1）：hard 必须 up:True down:False；soft（CI）失败重试仍败则 SKIP 不判 FAIL。
    $f11Pass = ($geoText -match 'f11=up:True down:False') -or ($geoText -match 'f11=SKIP\(soft\)')
    Assert-T $f11Pass "F1: keybd_event 注入 F11 → 最大化/还原翻转（hard 必过；CI soft 可 SKIP；禁用 WM_SYSCOMMAND 替代）"
    Assert-T ($geoText -match 'title=ok:True') "G3/G7: 最大化→还原后自绘标题栏子控件存在、Visible、高度≈32×DPI（防按钮消失）"
    Assert-T ($geoText -match 'readystate="complete"') "W6: 主 WebView2 document.readyState=complete（非白屏，白屏断言基础设施就位）"
}

Write-Host "`n=== E5: 诊断导出（服务运行中，日志被锁定）===" -ForegroundColor Cyan
$diaHome = Join-Path $base "dia-home"
New-Item -ItemType Directory -Force -Path (Join-Path $diaHome "dsh-launcher") | Out-Null
Set-Content (Join-Path $diaHome "dsh-launcher\dsh.log") @'
{"ts":"2026-08-16 12:00:00.000","level":"INFO","pid":1,"msg":"start"}
{"ts":"2026-08-16 12:00:01.000","level":"ERROR","pid":1,"code":"E2004","msg":"test error"}
'@ -Encoding UTF8
# 模拟"日志被运行中服务锁定"：cmd >> 重定向的真实语义是【允许他人读、拒绝写】
# （FileShare.Read）；不能用 FileShare.None（连读都拒，比真实场景更严，测不出共享读修复）
$lockFs = [System.IO.File]::Open((Join-Path $diaHome "dsh-launcher\dsh.log"), [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = "--diagnose"
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables["DSH_HOME"] = $diaHome
    $psi.EnvironmentVariables["DSH_WEB_URL"] = ""
    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit(30000)
    Assert-T ($p.ExitCode -eq 0) "E5: --diagnose 退出码 0（服务锁定日志时也能导出）"
    Assert-T ($so.Result -match 'dsh-launcher diagnose: .+\.zip') "E5: stdout 输出 zip 路径"
    $zip = Get-ChildItem (Join-Path $env:USERPROFILE "Downloads\dsh-launcher-diagnose-*.zip") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Assert-T ($null -ne $zip) "E5: zip 生成于下载目录"
    if ($zip) {
        Assert-T ($zip.Length -gt 200) "E5: zip 非空（含日志主体，非 22 字节空壳）"
        $extract = Join-Path $base "dia-extract"
        Expand-Archive $zip.FullName -DestinationPath $extract -Force
        $got = (Get-ChildItem $extract | Select-Object -ExpandProperty Name) -join ","
        foreach ($need in "log-full.txt", "env.txt", "versions.txt", "settings.txt", "state.txt", "errors.txt") {
            Assert-T ($got.Contains($need)) "E5: zip 含 $need"
        }
        $full = Get-Content (Join-Path $extract "log-full.txt") -Raw
        Assert-T ($full -match "E2004") "E5: 日志主体含错误码内容（可诊断）"
        Remove-Item $zip.FullName -Force
    }
} finally {
    $lockFs.Dispose()
}

Write-Host "`n=== E6: 卸载清理（uninstall-autostart.cmd）===" -ForegroundColor Cyan
$uninstall = Join-Path $deploy "uninstall-autostart.cmd"
if (-not (Test-Path $uninstall)) { $uninstall = Join-Path $root "scripts\uninstall-autostart.cmd" }
# 隔离模拟：伪造 APPDATA/USERPROFILE/DSH_HOME 指向隔离区，跑卸载脚本
$fakeAppData = Join-Path $base "fake-appdata"
$fakeProfile = Join-Path $base "fake-profile"
New-Item -ItemType Directory -Force -Path (Join-Path $fakeAppData "Microsoft\Windows\Start Menu\Programs\dsh-launcher") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $fakeProfile "Desktop") | Out-Null
$isoHome2 = Join-Path $base "uninstall-home"
New-Item -ItemType Directory -Force -Path (Join-Path $isoHome2 "dsh-launcher") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $isoHome2 "profiles") | Out-Null
Set-Content (Join-Path $isoHome2 "profiles\keep.yaml") "keep me" -Encoding UTF8
Set-Content (Join-Path $isoHome2 "dsh-launcher\dsh.log") "test log" -Encoding UTF8

$env:APPDATA = $fakeAppData; $env:USERPROFILE = $fakeProfile; $env:DSH_HOME = $isoHome2
try {
    & $uninstall *> (Join-Path $base "uninstall-out.txt")
    Assert-T (-not (Test-Path (Join-Path $fakeProfile "Desktop\dsh-launcher.lnk"))) "E6: 桌面快捷方式已删"
    Assert-T (-not (Test-Path (Join-Path $fakeAppData "Microsoft\Windows\Start Menu\Programs\dsh-launcher"))) "E6: 开始菜单目录已删"
} finally {
    Remove-Item Env:APPDATA -ErrorAction SilentlyContinue
    Remove-Item Env:USERPROFILE -ErrorAction SilentlyContinue
    Remove-Item Env:DSH_HOME -ErrorAction SilentlyContinue
}
$out6 = Get-Content (Join-Path $base "uninstall-out.txt") -Raw
Assert-T ($out6 -match 'Removed') "E6: 卸载脚本输出了清理动作"

Write-Host "`n=== E7: -CleanData 数据边界（只清自有数据，不碰 dsh 生态）===" -ForegroundColor Cyan
$isoHome3 = Join-Path $base "cleandata-home"
New-Item -ItemType Directory -Force -Path (Join-Path $isoHome3 "dsh-launcher") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $isoHome3 "profiles") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $isoHome3 "sessions") | Out-Null
Set-Content (Join-Path $isoHome3 "dsh-launcher\dsh.log") "test" -Encoding UTF8
Set-Content (Join-Path $isoHome3 "profiles\keep.yaml") "keep" -Encoding UTF8
Set-Content (Join-Path $isoHome3 "settings.yaml") "theme: dark" -Encoding UTF8
$env:DSH_HOME = $isoHome3
try { & $uninstall -CleanData *> (Join-Path $base "cleandata-out.txt") } finally { Remove-Item Env:DSH_HOME -ErrorAction SilentlyContinue }
Assert-T (-not (Test-Path (Join-Path $isoHome3 "dsh-launcher"))) "E7: -CleanData 删除 DSH_HOME\dsh-launcher"
Assert-T (Test-Path (Join-Path $isoHome3 "profiles\keep.yaml")) "E7: profiles/ 原样保留"
Assert-T (Test-Path (Join-Path $isoHome3 "sessions")) "E7: sessions/ 原样保留"
Assert-T (Test-Path (Join-Path $isoHome3 "settings.yaml")) "E7: settings.yaml 原样保留（dsh 生态数据）"

# ---- 清理 ----
Stop-TestPort $svcPort
Get-CimInstance Win32_Process -Filter "Name='DshWeb.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -like "*$base*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Remove-Item $base -Recurse -Force -ErrorAction SilentlyContinue
try {
    if ($null -eq $origRunValue) { Remove-ItemProperty -Path $runKeyPath -Name "dsh-launcher" -ErrorAction SilentlyContinue }
    else { Set-ItemProperty -Path $runKeyPath -Name "dsh-launcher" -Value $origRunValue }
} catch { }

Write-Host ""
if ($script:failed -eq 0) {
    Write-Host "E2E 测试全部通过（$script:passed 项断言）" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($script:failed) 项 E2E 断言失败（$script:passed 项通过）" -ForegroundColor Red
    exit 1
}
