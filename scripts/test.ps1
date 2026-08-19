<#
.SYNOPSIS
dsh-launcher 测试入口：单元测试 + 脚本/打包集成检查 + 可选冒烟测试。

.DESCRIPTION
默认运行：
  1. dotnet test（ShellLogic 单元测试）
  2. 静态回归断言（dsh-web.cmd 路径、uninstall 不再用 schtasks、vbs 命令正确）
  3. uninstall-autostart.cmd 行为测试（伪造 APPDATA/USERPROFILE，不触碰真实文件）

加 -Smoke 额外运行：
  4. 冒烟测试（需 dist\DshWeb.exe 存在且 3080 端口开放）：
     启动壳应用、校验窗口标题、验证单实例保护，然后自动关闭。

.EXAMPLE
./scripts/test.ps1
./scripts/test.ps1 -Smoke
#>
param(
    [switch]$Smoke
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$script:failed = 0

function Assert-True([bool]$Cond, [string]$Msg) {
    if ($Cond) { Write-Host "[ OK ] $Msg" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Msg" -ForegroundColor Red; $script:failed++ }
}

Write-Host "== 1. C# 单元测试 (dotnet test) ==" -ForegroundColor Cyan
$testOut = dotnet test (Join-Path $root "tests\DshShell.Tests") -c Release --nologo -v q 2>&1
$testCode = $LASTEXITCODE
$testOut | Select-Object -Last 12
Assert-True ($testCode -eq 0) "dotnet test 通过"

Write-Host "`n== 2. 脚本静态回归断言 ==" -ForegroundColor Cyan
$webCmd = Get-Content (Join-Path $root "scripts\dsh-web.cmd") -Raw
Assert-True ($webCmd -match '%DIR%DshWeb\.exe') "dsh-web.cmd 从脚本同目录启动 DshWeb.exe"
Assert-True ($webCmd -notmatch 'bin\\') "dsh-web.cmd 不再引用不存在的 bin\ 子目录"
Assert-True ($webCmd -match 'start-dsh\.vbs') "dsh-web.cmd 会调用 start-dsh.vbs"

$uninstall = Get-Content (Join-Path $root "scripts\uninstall-autostart.cmd") -Raw
Assert-True ($uninstall -notmatch 'schtasks') "uninstall-autostart.cmd 不再删除计划任务"
Assert-True ($uninstall -match 'Start Menu\\Programs\\Startup') "uninstall 删除启动文件夹自启项"
Assert-True ($uninstall -match 'dsh-autostart\.vbs') "uninstall 同时清理旧版 dsh-autostart.vbs"
Assert-True ($uninstall -match 'DshWeb\*\.lnk') "uninstall 删除桌面快捷方式"
Assert-True ($uninstall -match '-CleanData') "uninstall 提供显式 -CleanData 数据清理开关"
Assert-True ($uninstall -match 'rmdir /s /q "!DSH_HOME_P!\\dsh-launcher"') "uninstall -CleanData 只清 DSH_HOME\dsh-launcher（延迟扩展）"
Assert-True ($uninstall -match '!DSH_HOME_P!') "uninstall -CleanData 使用延迟扩展（防解析期空值误删盘根，历史事故回归断言）"
Assert-True ($uninstall -match 'EnableDelayedExpansion') "uninstall 启用延迟扩展"

$vbs = Get-Content (Join-Path $root "scripts\start-dsh.vbs") -Raw
Assert-True ($vbs -match 'dsh web --host 127\.0\.0\.1 --port " & port') "start-dsh.vbs 启动 dsh web (127.0.0.1:3080)"
Assert-True ($vbs -match 'DSH_LOG') "start-dsh.vbs 使用壳传入的统一日志路径（DSH_LOG）"
Assert-True ($vbs -match 'dsh-launcher\\dsh\.log') "start-dsh.vbs 回退路径也是统一 dsh.log"
Assert-True ($vbs -match 'OpenTextFile\(logfile, 8') "start-dsh.vbs 改为追加模式（8），不再截断"
Assert-True ($vbs -notmatch '\.dsh-web\.log') "start-dsh.vbs 不再写旧式 .dsh-web.log"
Assert-True ($vbs -match 'Chr\(34\)') "start-dsh.vbs 日志重定向加引号（防用户名含空格/元字符注入，S5）"
Assert-True ($vbs -match 'npx -y @deepseek-ai/dsh') "start-dsh.vbs 包含 npx 回退（dsh 不在 PATH 时）"

# v0.3.1：check-prereq.cmd 便携版环境自检（纯 cmd，零依赖）
$prereq = Get-Content (Join-Path $root "scripts\check-prereq.cmd") -Raw
Assert-True ($prereq -match 'WindowsDesktop\.App') "check-prereq.cmd 检测 .NET Desktop Runtime"
Assert-True ($prereq -match 'webview2|WebView2') "check-prereq.cmd 检测 WebView2 Runtime"
Assert-True ($prereq -match 'node --version') "check-prereq.cmd 检测 Node.js 18+"
Assert-True ($prereq -match '--diagnose') "check-prereq.cmd 指引 --diagnose 诊断导出"
$prereqBytes = [System.IO.File]::ReadAllBytes((Join-Path $root "scripts\check-prereq.cmd"))
$crCount = ($prereqBytes | Where-Object { $_ -eq 13 } | Measure-Object).Count
$lfCount = ($prereqBytes | Where-Object { $_ -eq 10 } | Measure-Object).Count
Assert-True ($crCount -eq $lfCount -and $crCount -gt 0) "check-prereq.cmd 使用 CRLF 换行（cmd 批处理硬性要求）"

# 自启=拉壳（Run 项直接指向 DshWeb.exe，壳自行拉起服务）：
# 壳源码 EnsureAutoStartRequested 写 DshWeb.exe（不再 wscript+vbs）
$shellSrc = Get-Content (Join-Path $root "src\DshShell\Program.cs") -Raw
Assert-True ($shellSrc -match 'Path\.Combine\(AppContext\.BaseDirectory, "DshWeb\.exe"\)') "壳自启写 DshWeb.exe（拉壳方案）"
Assert-True ($shellSrc -notmatch 'wscript\.exe.*start-dsh\.vbs.*HKCU') "壳自启不再用 wscript+start-dsh.vbs"
# v0.3.0 静态回归：统一日志 / 诊断导出 / 配置降级 / 延迟更新 / 错误码
Assert-True ($shellSrc -match 'Logger\.Init\(UnifiedLogPath\)') "壳启动初始化统一日志"
Assert-True ($shellSrc -match 'RotateIfNeeded\(\)') "壳启动早段执行日志轮转"
Assert-True ($shellSrc -match '--diagnose') "壳支持 --diagnose 诊断导出"
Assert-True ($shellSrc -match 'IsLifetimePluginInstalled') "壳检测 lifetime 插件（托盘/配置降级）"
Assert-True ($shellSrc -match 'StagedUpdate\.MarkPending') "壳实现 dsh 延迟应用更新（staged）"
Assert-True ($shellSrc -notmatch '\.dsh-web\.log') "壳不再引用旧式 .dsh-web.log 路径"

# ---- Task 0.2.5 完成态静态断言（重构收尾时启用，重构中保持"旧结构基线"锁定）----
# 目标（Step 6 收尾）：Program.cs 不再含 `: Form` 子类、WndProc、CreateParams、WebView2 事件接线，
# 窗体/WebView 逻辑下沉到 Managers/，Program.Main 退化为纯编排。
# ⚠️ 基线阶段下方断言锁定"当前仍是旧结构"，重构完成时【反转】为断言"已不再含以下字样"：
#   - Program.cs 不得含 `class DshShellForm : Form` / `: Form`
#   - Program.cs 不得含 `WndProc` / `CreateParams`
#   - Program.cs 不得含 `web.CoreWebView2.` 事件接线（PermissionRequested 等）
# 反转示例：
#   Assert-True ($shellSrc -notmatch ': Form')        "Program.cs 不含 Form 子类"
#   Assert-True ($shellSrc -notmatch 'WndProc')       "Program.cs 不含 WndProc"
#   Assert-True ($shellSrc -notmatch 'PermissionRequested') "WebView2 事件接线已迁出 Program"
Assert-True ($shellSrc -match 'class DshShellForm : Form') "【基线】Program.cs 当前仍含 DshShellForm:Form（重构前锁定；收尾反转）"
Assert-True ($shellSrc -match 'protected override void WndProc') "【基线】Program.cs 当前仍含 WndProc（重构前锁定；收尾反转）"
Assert-True ($shellSrc -match 'protected override CreateParams') "【基线】Program.cs 当前仍含 CreateParams（重构前锁定；收尾反转）"
# Step 4 完成：WebView2 事件接线已迁入 WebViewManager → 此断言提前反转（完成态）。
Assert-True ($shellSrc -notmatch 'web\.CoreWebView2\.(PermissionRequested|NewWindowRequested|DownloadStarting|NavigationStarting|ProcessFailed)') "【Step4完成】Program.cs 不含 WebView2 事件接线（已迁入 WebViewManager）"
# 卸载清理 CA：RemoveAutoRun 识别 DshWeb.exe 与 start-dsh.vbs 两种历史格式
$caSrc = Get-Content (Join-Path $root "installer\FolderPickerCa\FolderPickerCa.cs") -Raw
Assert-True ($caSrc -match 'DshWeb\.exe') "卸载 CA 清理 DshWeb.exe 自启值"
Assert-True ($caSrc -match 'start-dsh\.vbs') "卸载 CA 兼容清理旧版 start-dsh.vbs 自启值"
Assert-True ($caSrc -match 'CleanUserData') "卸载 CA 提供用户数据清理（仅自有数据）"
Assert-True ($caSrc -notmatch 'profiles.*Directory\.Delete') "卸载 CA 不删除 dsh profiles/插件目录"

Write-Host "`n== 3. uninstall-autostart.cmd 行为测试 ==" -ForegroundColor Cyan
$tmp = Join-Path $env:TEMP ("dsh-test-" + [guid]::NewGuid().ToString("N"))
try {
    # 覆盖 APPDATA 后脚本路径为 $tmp\Microsoft\Windows\Start Menu\Programs\Startup
    $startup = Join-Path $tmp "Microsoft\Windows\Start Menu\Programs\Startup"
    $desktop = Join-Path $tmp "Profile\Desktop"
    New-Item -ItemType Directory -Force -Path $startup, $desktop | Out-Null
    Set-Content (Join-Path $startup "start-dsh.vbs") "' fake"
    Set-Content (Join-Path $startup "dsh-autostart.vbs") "' fake legacy"
    Set-Content (Join-Path $desktop "DshWeb.lnk") "fake"
    Set-Content (Join-Path $desktop "DeepSeek Harness.lnk") "fake"
    Set-Content (Join-Path $desktop "keep.txt") "unrelated"

    $oldAp = $env:APPDATA; $oldUp = $env:USERPROFILE
    $env:APPDATA = $tmp
    $env:USERPROFILE = (Join-Path $tmp "Profile")
    cmd /c "`"$(Join-Path $root 'scripts\uninstall-autostart.cmd')`" < nul" | Out-Null
    $env:APPDATA = $oldAp; $env:USERPROFILE = $oldUp

    Assert-True (-not (Test-Path (Join-Path $startup "start-dsh.vbs"))) "删除启动文件夹自启项"
    Assert-True (-not (Test-Path (Join-Path $startup "dsh-autostart.vbs"))) "删除旧版 dsh-autostart.vbs"
    Assert-True (-not (Test-Path (Join-Path $desktop "DshWeb.lnk"))) "删除 DshWeb.lnk"
    Assert-True (-not (Test-Path (Join-Path $desktop "DeepSeek Harness.lnk"))) "删除 DeepSeek Harness.lnk"
    Assert-True (Test-Path (Join-Path $desktop "keep.txt")) "不影响无关文件"
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n== 3.5. -CleanData 数据清理行为测试（DSH_HOME/USERPROFILE 均隔离到 %TEMP%） ==" -ForegroundColor Cyan
$tmp2 = Join-Path $env:TEMP ("dsh-clean-" + [guid]::NewGuid().ToString("N"))
try {
    $fakeHome = Join-Path $tmp2 "dshhome"
    $dataDir = Join-Path $fakeHome "dsh-launcher"
    $profileDir = Join-Path $fakeHome "profiles\web"
    New-Item -ItemType Directory -Force -Path $dataDir, $profileDir | Out-Null
    Set-Content (Join-Path $dataDir "dsh.log") "fake log"
    Set-Content (Join-Path $dataDir "service-pid-99999.txt") "99999"
    Set-Content (Join-Path $profileDir "package.json") '{"keep":true}'

    # 防护断言：伪造 DSH_HOME 必须落在 %TEMP% 内才允许执行脚本（防误删真实数据）
    $fakeHomeFull = [System.IO.Path]::GetFullPath($fakeHome)
    $tempFull = [System.IO.Path]::GetFullPath($env:TEMP)
    Assert-True ($fakeHomeFull.StartsWith($tempFull, [System.StringComparison]::OrdinalIgnoreCase)) "伪造 DSH_HOME 必须位于 %TEMP% 内"

    $oldDshHome = $env:DSH_HOME; $oldUp2 = $env:USERPROFILE
    try {
        $env:DSH_HOME = $fakeHome
        $env:USERPROFILE = (Join-Path $tmp2 "Profile")
        # 1) 不带 -CleanData：数据目录必须保留（默认不删数据）
        cmd /c "`"$(Join-Path $root 'scripts\uninstall-autostart.cmd')`" < nul" | Out-Null
        Assert-True (Test-Path (Join-Path $dataDir "dsh.log")) "默认运行不删除数据目录"

        # 2) 带 -CleanData：数据目录被清、profiles 里的"用户文件"保留
        cmd /c "`"$(Join-Path $root 'scripts\uninstall-autostart.cmd')`" -CleanData < nul" | Out-Null
        Assert-True (-not (Test-Path $dataDir)) "-CleanData 删除 DSH_HOME\dsh-launcher"
        Assert-True (Test-Path (Join-Path $profileDir "package.json")) "-CleanData 不触碰 profiles/ 插件数据"
    }
    finally {
        $env:DSH_HOME = $oldDshHome; $env:USERPROFILE = $oldUp2
    }
}
finally {
    Remove-Item $tmp2 -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Smoke) {
    Write-Host "`n== 4. 冒烟测试 ==" -ForegroundColor Cyan
    $zip = (Get-ChildItem (Join-Path $root "dist\dsh-launcher-windows-*.zip") -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    Assert-True ($null -ne $zip) "dist\dsh-launcher-windows-<版本>.zip 存在（先运行 ./scripts/build-release.ps1）"
    if ($zip -and (Test-Path $zip)) {
        $smokeDir = Join-Path $env:TEMP ("dsh-smoke-" + [guid]::NewGuid().ToString("N"))
        try {
            Expand-Archive -Path $zip -DestinationPath $smokeDir -Force
            $exe = Join-Path $smokeDir "DshWeb.exe"
            Assert-True (Test-Path $exe) "发布包解压后包含 DshWeb.exe"
            Assert-True (Test-Path (Join-Path $smokeDir "start-dsh.vbs")) "发布包解压后包含 start-dsh.vbs"

            $portOpen = $false
            try { $c = New-Object Net.Sockets.TcpClient; $c.Connect("127.0.0.1", 3080); $portOpen = $true; $c.Close() } catch { }
            if (-not $portOpen) {
                Write-Host "[SKIP] 3080 端口未开放，跳过冒烟测试（需要 dsh 服务在运行）" -ForegroundColor Yellow
            }
            else {
                $app = Start-Process $exe -PassThru
                Start-Sleep -Seconds 8
                $app.Refresh()
                Assert-True (-not $app.HasExited) "壳应用启动后保持运行"
                Assert-True ($app.MainWindowTitle -eq "DeepSeek Harness") "主窗口标题为 'DeepSeek Harness'（实际：'$($app.MainWindowTitle)'）"

                $app2 = Start-Process $exe -PassThru
                Start-Sleep -Seconds 3
                $app2.Refresh()
                Assert-True ($app2.HasExited) "第二次启动立即退出（单实例保护）"

                # 清理：结束壳应用及其 WebView2 子进程
                Get-CimInstance Win32_Process -Filter "ParentProcessId = $($app.Id)" -ErrorAction SilentlyContinue |
                    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
                Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
                Write-Host "[ OK ] 冒烟测试窗口已关闭" -ForegroundColor Green
            }
        }
        finally {
            Remove-Item $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ""
if ($script:failed -eq 0) {
    Write-Host "全部测试通过" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "$($script:failed) 项测试失败" -ForegroundColor Red
    exit 1
}
