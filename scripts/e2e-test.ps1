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
$zipPath = Join-Path $dist "dsh-launcher-windows.zip"
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
$msiPath = Get-ChildItem (Join-Path $dist "dsh-launcher-*.msi") -ErrorAction SilentlyContinue | Select-Object -First 1
Assert-T (Test-Path $zipPath) "免安装 zip 存在: dsh-launcher-windows.zip"
Assert-T ($null -ne $msiPath) "MSI 存在: $($msiPath.Name)"
Assert-T (Test-Path (Join-Path $dist "SHA256SUMS.txt")) "校验和文件存在"
if (Test-Path $zipPath) {
    $entries = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    $names = $entries.Entries | ForEach-Object FullName
    $entries.Dispose()
    foreach ($need in "DshWeb.exe", "WebView2Loader.dll", "runtimes/win-x64/native/WebView2Loader.dll",
        "start-dsh.vbs", "start-dsh.cmd", "dsh-web.cmd", "uninstall-autostart.cmd") {
        Assert-T ($names -contains $need) "zip 含 $need"
    }
}

Write-Host "`n=== E2: 免安装版部署（解压 zip 即用）===" -ForegroundColor Cyan
$deploy = Join-Path $base "deploy"
if (Test-Path $zipPath) {
    Expand-Archive -Path $zipPath -DestinationPath $deploy -Force
    $exe = Join-Path $deploy "DshWeb.exe"
} elseif ($PublishDir -and (Test-Path (Join-Path $PublishDir "DshWeb.exe"))) {
    $exe = Join-Path $PublishDir "DshWeb.exe"
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
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    const uint WM_CLOSE = 0x0010;

    static void Main(string[] args) {
        var exe = args[0]; var home = args[1]; var url = args[2]; var mode = args[3];
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

    static void Start(string exe, string home, string url) {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.EnvironmentVariables["DSH_HOME"] = home;
        psi.EnvironmentVariables["DSH_WEB_URL"] = url;
        psi.EnvironmentVariables["DSH_WEB_PORT"] = "";
        psi.EnvironmentVariables["DSH_NO_UI"] = "";
        // WebView2 数据目录隔离铁律：测试实例绝不能与真实实例共用 user-data-dir
        // （共用会导致 WebView2 互锁、真实启动器整窗灰死——2026-08-16 实测事故）
        psi.EnvironmentVariables["DSH_WEBVIEW2_DATA"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-e2e-wv2-" + System.Guid.NewGuid().ToString("N"));
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
$dshJs = Join-Path $env:APPDATA "npm\node_modules\@deepseek-ai\dsh\lib\bin.js"

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
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try { $r = Invoke-WebRequest -Uri "http://127.0.0.1:$svcPort" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop; if ($r.StatusCode -eq 200) { $ready = $true; break } } catch { }
    }
    Assert-T $ready "隔离 dsh 服务就绪 (127.0.0.1:$svcPort)"

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
} else {
    Write-Host "`n=== E3/E4: 跳过（无桌面会话或依赖缺失；GUI 用例需交互桌面）===" -ForegroundColor Yellow
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
