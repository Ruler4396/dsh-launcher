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
    [switch]$Smoke,
    [switch]$RealNet
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$script:failed = 0

function Assert-True([bool]$Cond, [string]$Msg) {
    if ($Cond) { Write-Host "[ OK ] $Msg" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Msg" -ForegroundColor Red; $script:failed++ }
}

# ---- 环境卫生（本地开发防污染，非削弱断言）：本脚本常被 dsh 壳派生的终端拉起，进程级
# 注入的 DSH_WEB_URL 会让 LauncherApp 误判"外部托管"从而短路端口三重验证（Zombie/Foreign
# 场景测试全红），DSH_VERSION/DSH_SERVICE_CMD 等也会劫持启动决策分支。单测必须从零环境
# 出发；CI 本就没有这些变量，本块零副作用。
Write-Host "== 0. 环境卫生（清除壳注入的 DSH_* 进程变量）==" -ForegroundColor Cyan
foreach ($hygieneVar in 'DSH_WEB_URL','DSH_WEB_PORT','DSH_VERSION','DSH_TEST_SPLASH_DELAY_MS',
                        'DSH_SERVICE_CMD','DSH_SANDBOX','DSH_NO_UI','DSH_E2E','DSH_TEST_FORCE_MANAGED',
                        'DSH_TEST_FAKE_APPLY','DSH_TEST_INSTANCE','DSH_PROFILE') {
    if (Test-Path "Env:$hygieneVar") {
        Write-Host ("  [clean] " + $hygieneVar)
        Remove-Item "Env:$hygieneVar" -ErrorAction SilentlyContinue
    }
}

Write-Host "== 1. C# 单元测试 (dotnet test) ==" -ForegroundColor Cyan
# 任务二硬门禁：DSH_FORCE_NPM_SMOKE=1 强制 RealWorldNpmExecutionTests 真实执行
#（无 Mock 直接跑 node.exe + npm-cli.js）。本机若无 Node 环境该测试将**失败**并阻断，
# 打破"测试幻觉"——本地验证真实 npm 链路必须可用（CI 无 Node 时该变量未设，测试自动跳过）。
$env:DSH_FORCE_NPM_SMOKE = "1"
# -RealNet：显式开启重型真实网络全链路用例（DshUpdatePipelineRealTests，分钟级、依赖镜像可达性）。
# 默认关闭——CI build 流水线总是调用本脚本，若默认开启会把发布门禁劫持给外部网络状况。
if ($RealNet) { $env:DSH_FORCE_REALNET = "1" } else { Remove-Item Env:DSH_FORCE_REALNET -ErrorAction SilentlyContinue }
$testOut = dotnet test (Join-Path $root "tests\DshShell.Tests") -c Release --nologo -v q 2>&1
$testCode = $LASTEXITCODE
$testOut | Select-Object -Last 12
Assert-True ($testCode -eq 0) "dotnet test 通过（含真实环境冒烟测试）"

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
# ---- v0.4.x 安全模式/浏览器自启修复后的启动形态：三分支统一经 bootMode 变量拼装
#      （web 子命令 / ADR-022 安全模式 --profile），并强制 --no-open（壳自管 WebView2 窗口，
#       防 dsh web 默认 ShellExecute 拉起系统浏览器弹同窗）----
Assert-True ($vbs -match '& bootMode & " --host 127\.0\.0\.1 --port " & port & " --no-open"') "start-dsh.vbs 三分支统一以 bootMode 启动 web（host/port/--no-open 一致）"
Assert-True ($vbs -match 'bootMode = "web"') "start-dsh.vbs 默认 bootMode 为 dsh web 子命令"
Assert-True ($vbs -match '--profile ') "start-dsh.vbs 支持安全模式 --profile 注入（ADR-022）"
Assert-True ($vbs -match '--no-open') "start-dsh.vbs 全分支携带 --no-open（防系统浏览器弹同窗）"
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
# 更新引擎内核已抽至 DshUpdateManager（2026-09 RealOS 可测性抽离）——相关断言扫描"Program+Manager 拼接源"，语义等价
$updateCoreSrc = $shellSrc + (Get-Content (Join-Path $root "src\DshShell\Managers\DshUpdateManager.cs") -Raw)
# 【ADR-024】双轨制收敛：进程/npm 执行原语迁至 ProcessRunner、服务生命周期迁至 ServiceLifecycleOps。
# 迁移类断言扫描"引擎联合源"（Program + 更新引擎 + 进程原语 + 服务管理），语义等价不削弱：
# 断言锁定的不变式（超时上限/异步排空/工作目录等）必须存在于系统某处，且 Program 本体被
# 下方 2.2 节双轨制门禁禁止重新收留这些原语。
$processRunnerSrc = Get-Content (Join-Path $root "src\DshShell\Managers\ProcessRunner.cs") -Raw
$serviceMgrSrc = Get-Content (Join-Path $root "src\DshShell\Managers\ServiceManager.cs") -Raw
$lifecycleOpsSrc = Get-Content (Join-Path $root "src\DshShell\Managers\ServiceLifecycleOps.cs") -Raw
$engineSrc = $updateCoreSrc + $processRunnerSrc + $serviceMgrSrc + $lifecycleOpsSrc
$appEnvSrc = Get-Content (Join-Path $root "src\DshShell\Managers\AppEnvironment.cs") -Raw
$jsEntrySrc = Get-Content (Join-Path $root "src\DshShell\Domain\JsEntryResolver.cs") -Raw
Assert-True ($appEnvSrc -match 'Path\.Combine\(AppContext\.BaseDirectory, "DshWeb\.exe"\)') "壳自启写 DshWeb.exe（拉壳方案；实现现居 AppEnvironment.EnsureAutoStartRequested）"
Assert-True ($shellSrc -notmatch 'wscript\.exe.*start-dsh\.vbs.*HKCU') "壳自启不再用 wscript+start-dsh.vbs"
# v0.3.0 静态回归：统一日志 / 诊断导出 / 配置降级 / 延迟更新 / 错误码
Assert-True ($shellSrc -match 'Logger\.Init\(UnifiedLogPath\)') "壳启动初始化统一日志"
Assert-True ($shellSrc -match 'RotateIfNeeded\(\)') "壳启动早段执行日志轮转"
Assert-True ($shellSrc -match '--diagnose') "壳支持 --diagnose 诊断导出"
Assert-True ($appEnvSrc -match 'IsLifetimePluginInstalled') "壳检测 lifetime 插件（托盘/配置降级；探测现居 AppEnvironment.ReadLifetimeMode）"
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
# Step 6 完成：窗体类（DshShellForm/TrayMenuForm）已迁出至 Windows/，以下完成态断言启用。
# 匹配类声明 `: Form`（精确类继承，避免误匹配 FormWindowState/FormBorderStyle 等标识符）
Assert-True ($shellSrc -notmatch '(class|record|struct)\s+\w+\s*:\s*Form\b') "【Step6完成】Program.cs 不含 Form 子类（已迁出 Windows/）"
Assert-True ($shellSrc -notmatch 'WndProc') "【Step6完成】Program.cs 不含 WndProc（已迁出 Windows/）"
Assert-True ($shellSrc -notmatch 'CreateParams') "【Step6完成】Program.cs 不含 CreateParams（已迁出 Windows/）"
# Step 4 完成：WebView2 事件接线已迁入 WebViewManager → 此断言提前反转（完成态）。
Assert-True ($shellSrc -notmatch 'web\.CoreWebView2\.(PermissionRequested|NewWindowRequested|DownloadStarting|NavigationStarting|ProcessFailed)') "【Step4完成】Program.cs 不含 WebView2 事件接线（已迁入 WebViewManager）"
# ADR-021：严禁使用 cmd.exe 包装 Node.js 脚本，必须使用 node.exe 直接执行 .js 入口
# 扫描 src/ 下所有 .cs 文件，排除注释行（以 // 开头），检查实际代码中是否出现 cmd.exe 调用
$srcCsFiles = Get-ChildItem (Join-Path $root "src") -Recurse -Filter "*.cs"
$cmdExeViolations = @()
foreach ($f in $srcCsFiles) {
    $lines = Get-Content $f.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].Trim()
        # 跳过注释行
        if ($line.StartsWith('//')) { continue }
        if ($line.StartsWith('///')) { continue }
        # 检查实际代码中的 cmd.exe 调用（排除字符串字面量中的描述性文本）
        if ($line -match 'ProcessStartInfo.*cmd\.exe|"cmd\.exe"|cmd\.exe.*/c') {
            $cmdExeViolations += "$($f.Name):$($i+1): $line"
        }
    }
}
Assert-True ($cmdExeViolations.Count -eq 0) "【ADR-021】src/ 中严禁出现 cmd.exe 调用（$(if($cmdExeViolations.Count -gt 0){$cmdExeViolations[0]}else{'clean'})"
# 技术债门禁：扫描常见反模式
foreach ($f in $srcCsFiles) {
    $content = Get-Content $f.FullName -Raw
    # DoEvents 重入风险（CI 自测路径除外）
    if ($f.Name -ne "Program.cs") {
        Assert-True ($content -notmatch 'DoEvents\(\)') "【技术债】$($f.Name) 不含 DoEvents"
    }
    # Assembly.Location 在 SingleFile 下返回空字符串
    Assert-True ($content -notmatch 'Assembly\.Location') "【技术债】$($f.Name) 不含 Assembly.Location（SingleFile 不兼容）"
}
# Kill() 必须带 entireProcessTree（扫描所有 .cs 文件）
$killViolations = @()
foreach ($f in $srcCsFiles) {
    $lines = Get-Content $f.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].Trim()
        if ($line.StartsWith('//') -or $line.StartsWith('///')) { continue }
        if ($line -match '\.Kill\(\)' -and $line -notmatch 'entireProcessTree|Kill\(true\)') {
            $killViolations += "$($f.Name):$($i+1): $line"
        }
    }
}
Assert-True ($killViolations.Count -eq 0) "【技术债】Kill() 必须带 entireProcessTree（$(if($killViolations.Count -gt 0){$killViolations[0]}else{'clean'})"
# 卸载清理 CA：RemoveAutoRun 识别 DshWeb.exe 与 start-dsh.vbs 两种历史格式
$caSrc = Get-Content (Join-Path $root "installer\FolderPickerCa\FolderPickerCa.cs") -Raw
Assert-True ($caSrc -match 'DshWeb\.exe') "卸载 CA 清理 DshWeb.exe 自启值"
Assert-True ($caSrc -match 'start-dsh\.vbs') "卸载 CA 兼容清理旧版 start-dsh.vbs 自启值"
Assert-True ($caSrc -match 'CleanUserData') "卸载 CA 提供用户数据清理（仅自有数据）"
Assert-True ($caSrc -notmatch 'profiles.*Directory\.Delete') "卸载 CA 不删除 dsh profiles/插件目录"

# ---- v0.4.0 生产修复契约（僵尸端口/日志锁/更新进度）静态断言：
# 即使单测被跳过，CI 也能锁定关键代码路径的存在性与抗回退基线。----
$logicSrc = Get-Content (Join-Path $root "src\DshShell\ShellLogic.cs") -Raw
Assert-True ($logicSrc -match 'GetProcessIdByPort') "壳含端口→PID 反查（GetExtendedTcpTable/netstat，僵尸端口归属验证）"
Assert-True ($logicSrc -match 'GetExtendedTcpTable') "壳含 P/Invoke GetExtendedTcpTable（精确端口归属）"
Assert-True ($logicSrc -match 'KillProcessTree') "壳含进程树强杀（taskkill /T /F 语义）"
Assert-True ($logicSrc -match 'GetAncestorPids') "壳含祖先进程链（清理 cmd/npx 外壳）"
Assert-True ($engineSrc -match 'IsRetryableNpmError') "系统含 npm 失败可重试判定（pending 保留/清理策略；现居更新引擎）"
Assert-True ($shellSrc -match 'NotifyUpdateApplyFailed') "壳含更新失败用户通知（E4002 弹窗收口，策略在引擎）"
Assert-True ($engineSrc -match '正在应用更新 \(v') "壳含更新安装进度上报（Splash '正在应用更新 (vX)…'；现居引擎）"
Assert-True ($engineSrc -match 'BeginOutputReadLine') "npm 实时日志逐行异步读取（非一次性 ReadToEnd；现居 ProcessRunner）"

$loggerSrc = Get-Content (Join-Path $root "src\DshShell\Logger.cs") -Raw
Assert-True ($loggerSrc -match 'FileShare\.ReadWrite') "Logger 用 FileShare.ReadWrite 防日志锁死（兼容 cmd >> 句柄）"
Assert-True ($loggerSrc -match 'dsh-launcher-fallback') "Logger 含 %TEMP% fallback 路径（主日志被锁时落盘）"
Assert-True ($loggerSrc -match 'FATAL LOGGER') "Logger fallback 时输出 Console.Error 醒目告警"

$serviceMgr = Get-Content (Join-Path $root "src\DshShell\Managers\ServiceManager.cs") -Raw
Assert-True ($serviceMgr -match 'ProbePort') "ServiceManager 含端口三重验证（TCP+进程身份+HTTP）"
Assert-True ($serviceMgr -match 'KillZombieTree') "ServiceManager 含僵尸进程树清理"
Assert-True ($serviceMgr -match 'ServicePortState\.(Healthy|Zombie|Foreign)') "ServiceManager 区分 Healthy/Zombie/Foreign 三态"

$splashSrc = Get-Content (Join-Path $root "src\DshShell\Windows\SplashForm.cs") -Raw
Assert-True ($splashSrc -match 'IsApplyingUpdate') "Splash 支持更新安装阶段标志（取消按钮禁用/安装中…）"

# v0.4.0 更新文案预期管理：tarball 缺失回退现场下载时，Splash 如实显示"预计 1-2 分钟"耗时
Assert-True ($shellSrc -match '预计 1-2 分钟') "更新应用 Splash 文案明示现场下载耗时（预计 1-2 分钟），诚实管理预期"

# ---- v0.4.0 更新链路改进（后台静默下载 + 本地 tarball 直装）静态断言 ----
Assert-True ($shellSrc -match 'LocateTarball') "壳含本地 tarball 定位（应用优先本地安装包，不现场拉取）"
Assert-True ($shellSrc -match 'var installSpec = localTarball \?\?') "壳含安装来源选择：本地 tarball 优先、缺失回退 registry spec（来源可诊断）"
Assert-True ($shellSrc -match '后台静默下载') "更新询问弹窗明示'后台静默下载'（不打断当前使用）"
Assert-True ($shellSrc -match '需联网解析依赖') "更新气泡/弹窗文案如实'需联网解析依赖，预计 1-2 分钟'（不误导'已全部下载完'）"
Assert-True ($shellSrc -match '主程序已下载') "更新文案区分'主程序已下载'与'依赖在线解析'（诚实管理预期）"
# ---- v0.4.x 更新引擎（staging 隔离构建 + 原子切换；替代旧 prefetch_temp 预热管线）静态断言 ----
# （2026-09 现代化：v0.4.0 已用"npm pack → staging/runtime-build 完整构建 → runtimes 原子搬移"
#   取代 npm-pack+预热临时目录方案，以下断言随架构升级改锁新不变式，语义等价不削弱。）
Assert-True ($shellSrc -match 'runtime-build-') "下载管线在隔离 staging buildDir 构建运行时（不污染生产 runtimes）"
Assert-True ($shellSrc -match 'TryDeleteDir\(buildDir\)') "每次构建前强制清场 buildDir（防残留 lockfile 导致 pnpm 假成功）"
Assert-True ($shellSrc -match 'pointing at buildDir being rebuilt') "重建前清掉指向本 buildDir 的 stale pending（防半成品被强制应用）"
Assert-True ($updateCoreSrc -match '--prefix') "npm 回退安装走 --prefix 局部树（不触碰全局环境）"
Assert-True ($shellSrc -match '--no-audit --no-fund') "安装统一 --no-audit --no-fund（跳过审计/fund，加速安装）"
Assert-True ($shellSrc -match 'GetNpmRegistrySources') "pack/build/apply 共用同一源序列 GetNpmRegistrySources（防跨 registry cache miss）"
Assert-True ($shellSrc -match 'preserving tarball for next launch retry') "构建失败保留 tarball 待下次重试（降级不断链路）"
Assert-True ($updateCoreSrc -match 'timeoutMs: 1200000') "npm 构建路径有超时上限（强制 kill，不留僵尸树；内核现居 DshUpdateManager）"
# ---- v0.4.0 npm 执行引擎（node.exe 直接执行 npm-cli.js，彻底绕过 npm.cmd/cmd.exe）静态断言
#      【ADR-024】探测原语迁至 Domain/JsEntryResolver.ResolveNpmCliJs、执行原语迁至 ProcessRunner ----
Assert-True ($jsEntrySrc -match 'ResolveNpmCliJs') "npm-cli.js 探测存在（node.exe 同级 + AppData 全局两优先级；现居 JsEntryResolver）"
Assert-True ($engineSrc -match 'RunProcessCaptured\(nodeEnv\.NodeExe') "RunNpmCommand 用 node.exe 绝对路径启动（降维打击：绕过 .cmd/.bat/cmd.exe 全部陷阱）"
Assert-True ($engineSrc -match 'internal static bool RunProcessCaptured') "底层进程执行器 RunProcessCaptured 存在（供 Real-OS 测试零 Mock 调用；现居 ProcessRunner）"
Assert-True ($shellSrc -notmatch '(?m)^\s*chcp\s+65001') "已彻底删除 chcp 65001 Hack 代码（编码冲突根除，注释保留说明无害）"
Assert-True ($shellSrc -notmatch '/c \\"" \+ npmCmd') "已删除 cmd /c 双层引号 Hack（node 引擎替代）"
Assert-True ($engineSrc -match '未检测到可用的 Node\.js 环境') "node.exe 缺失时给出明确错误（不继续执行；现居 ProcessRunner）"
Assert-True ($engineSrc -match '未找到 npm-cli\.js') "npm-cli.js 缺失时给出明确错误（提示重装 Node；现居 ProcessRunner）"
Assert-True ($engineSrc -match 'StandardErrorEncoding = System\.Text\.Encoding\.UTF8') "stderr 显式 UTF-8（npm≥7 内部即 UTF-8，任何代码页可读）"
Assert-True ($shellSrc -match '原因：\{reason\}') "下载失败弹窗暴露真实 errorTail（不再硬编码'下载失败'藏原因）"
Assert-True ($shellSrc -match 'IsNpmNotFoundError') "错误分类纯函数（npm 环境缺失 vs 网络/registry，不同建议文案）"
$logicSrc = Get-Content (Join-Path $root "src\DshShell\ShellLogic.cs") -Raw
Assert-True ($logicSrc -match 'IsNpmNotFoundError') "ShellLogic 提供 npm 缺失判定纯函数（契约测试锁定）"
# ---- 预热工作目录修复断言随架构升级改锁新形态：npm 回退构建必须显式传 buildDir 工作目录
#      （相对路径 ./<tarball> 依赖该目录；ENOENT 根因同类，工作域从 prefetch_temp 迁移到 buildDir）
#      【ADR-024】实现现居 ProcessRunner.RunNpmCommand ----
Assert-True ($engineSrc -match 'WorkingDirectory = workingDirectory') "RunNpmCommand 支持工作目录参数（相对路径 ./<tarball> 依赖该目录；现居 ProcessRunner）"
Assert-True ($updateCoreSrc -match 'workingDirectory: buildDir') "npm 构建传入 staging buildDir 为工作目录（相对路径 ./<tarball> 依赖该目录；内核现居 DshUpdateManager）"
# ---- 下载秒败"文件名、目录名或卷标语法不正确"根因修复断言随架构升级改锁新形态：
#      pack 目标目录先创建（buildDir 由 Directory.CreateDirectory 保证存在）----
Assert-True ($shellSrc -match 'Directory\.CreateDirectory\(buildDir\)') "pack 前先创建目标构建目录（历史 ERROR_INVALID_NAME 场景的等价修复）"
$stagedSrc = Get-Content (Join-Path $root "src\DshShell\StagedUpdate.cs") -Raw
Assert-True ($stagedSrc -match 'LocateTarball') "StagedUpdate 提供本地 tarball 定位（三级：pending 名→命名规则→glob）"
Assert-True ($stagedSrc -match 'tarball\s*=\s*string\.IsNullOrWhiteSpace') "pending-update.json 记录 tarball 文件名（应用失败重试仍用本地包）"
Assert-True ($stagedSrc -match 'PrefetchTempDir') "StagedUpdate 保留 prefetch_temp 目录定义（旧 pending 记录兼容清理）"
# ---- v0.4.0 诚实承诺铁律（用户反馈：cache 未预热时文案却写"预计 5-10 秒"→ 120s 超时）静态断言 ----
Assert-True ($stagedSrc -match 'prefetched') "pending-update.json 保留 prefetched 标志（旧记录向后兼容；真实状态才为 true）"
Assert-True ($shellSrc -notmatch '依赖已预热，预计') "禁止'依赖已预热，预计 5-10 秒'虚假承诺（文案必须基于真实进度，不得写死秒数）"
Assert-True ($shellSrc -match 'ComposeTerminalTitleText') "构建成功/失败终态文本统一经 ComposeTerminalTitleText（结论驻留标题栏，不再静默消失）"
Assert-True ($engineSrc -match '可能需要几分钟') "线上回退路径如实提示'可能需要几分钟'（管理预期，不写死 1-2 分钟；文案现居引擎 Apply 路径 B）"

# ---- 2026-09 静默失败收口 + 首装全局安装 + 发布闸门 + 测试确定性（新增断言，只增不弱）
#      【ADR-024】首装链实现迁至 DshUpdateManager.EnsureDshInstalled（原 TryEnsureGlobalDshInstalled）----
Assert-True ($updateCoreSrc -match 'EnsureDshInstalled') "首装（无 dsh）改走 npm 全局安装（替代 SelfContained 双份构建，失败不静默落 npx；现居更新引擎）"
Assert-True ($updateCoreSrc -match 'DSH_TEST_ALLOW_GLOBAL_INSTALL') "首装全局安装带沙盒门控（CI/沙盒默认跳过真实网络安装）"
Assert-True ($engineSrc -match 'InvalidateCache\(\)') "首装安装成功后失效发现层记忆（新装 shim/版本立即可见）"
Assert-True ($shellSrc -match 'TryShowFatalDialog') "终态崩溃可见化：UnhandledException → [E9001] 弹窗（非无头模式），双击无声消失有线索"
$errCodes = Get-Content (Join-Path $root "src\DshShell\ErrorCodes.cs") -Raw
Assert-True ($errCodes -match 'E1009 =>' ) "[E1009] 带 Describe（第二实例主窗未就绪的 Info 弹窗）"
Assert-True ($errCodes -match 'E1012 =>' ) "[E1012] 带 Describe（首装 npm 全局安装失败的真实根因展示）"
$bpyml = Get-Content (Join-Path $root ".github\workflows\build.yml") -Raw
Assert-True ($bpyml -match 'missing-changelog' -and $bpyml -match 'exit 1') "发布闸门：tag 提交缺 CHANGELOG 条目时 fail-fast（v0.4.0 占位文案事故根治）"
Assert-True ($bpyml -match "contains\(github\.ref_name, '-'\)") "预发布 tag（含 '-'）整体跳过自动流水线（rc 由手动 gh --prerelease 发布，不抢正式版）"
$xunitCfg = Get-Content (Join-Path $root "tests\DshShell.Tests\xunit.runner.json") -Raw
Assert-True ($xunitCfg -match '"parallelizeTestCollections"\s*:\s*false') "单测已禁用集合间并行（StagedUpdate 静态状态互踩随机红根治）"

# ---- Sandbox (DSH_SANDBOX) 静态断言：门控 + 环境覆盖 ----
# ---- 【ADR-024】双轨制门禁：Program.cs 组合根纯净度（CI 红线） ----
# 铁律：Main/组合根只允许"环境初始化 + 装配 + 消息泵"。业务原语（进程拉起、HTTP 客户端、
# 文件删除、注册表读写、端口探测）一旦回流 Program.cs，Manager 依赖方向即被架空，
# 双轨制（vbs 旧链 vs Identity 新链）就会复活。以下 token 在 Program.cs 的**实际代码行**
# 中出现任意一个 → CI 立即标红。注释行豁免；限定名调用（如 ShellLogic.ServiceReadiness.PortOpen）
# 不算违例——组合根允许经 Manager/纯函数间接使用原语，禁止的是**直接持有**。
Write-Host "`n== 2.2. 双轨制门禁（ADR-024：Program.cs 组合根纯净度） ==" -ForegroundColor Cyan
$programLines = Get-Content (Join-Path $root "src\DshShell\Program.cs")
$bannedPatterns = @(
    @('new\s+HttpClient|HttpClient\s*\{',                     'HTTP 客户端（应经 Managers.WebRuntimeInstaller.CreateHttpClient）'),
    @('new\s+ProcessStartInfo|Process\.Start\(',              '进程拉起（应经 ServiceManager.Start / ProcessRunner / WebRuntimeInstaller.OpenExternally）'),
    @('cmd\.exe|wscript',                                     'cmd.exe/wscript 中间层（ADR-021/024 双轨制已消灭）'),
    @('taskkill|msiexec',                                     '外部工具直调（应经 ShellLogic.ProcessManagement / Windows.LegacyUpgradeCleanup）'),
    @('File\.Delete\(|Directory\.Delete\(',                   '删除原语（应经 Managers.ProcessRunner.TryDeleteDir 等引擎收口）'),
    @('Registry\.|Microsoft\.Win32\.Registry',                '注册表读写（应经 AppEnvironment / LegacyUpgradeCleanup）'),
    @('(?<!\.)\bPortOpen\s*\(',                               '裸 PortOpen 探测（只允许 ShellLogic.ServiceReadiness.PortOpen 限定名）'),
    @('\bTcpClient\b',                                        'TCP 客户端（就绪探测属 ServiceManager.PollReadiness）'),
    @('File\.WriteAllText\(',                                 '文件写入原语（核心状态走 ShellLogic.FileSystemPolicy.AtomicWrite，其余经 Manager）')
)
$dualTrackViolations = @()
foreach ($pl in $programLines) {
    $line = $pl.Trim()
    if ($line.StartsWith('//') -or $line.StartsWith('///') -or $line.StartsWith('*')) { continue }
    $codeOnly = $line -replace '\s+//.*$', ''   # 剥离行尾注释（字符串字面量含 // 的场景由人工评审兜底）
    foreach ($bp in $bannedPatterns) {
        if ($codeOnly -match $bp[0]) { $dualTrackViolations += "$($bp[1]) => $line"; break }
    }
}
Assert-True ($dualTrackViolations.Count -eq 0) "【ADR-024】Program.cs 零业务原语（$(if($dualTrackViolations.Count -gt 0){'首例: ' + $dualTrackViolations[0]}else{'clean'})"
# 正面断言：Identity 直启链在组合根可见（StartDshServiceViaIdentity 是唯一服务拉起入口名）
Assert-True ($shellSrc -match 'StartDshServiceViaIdentity') "组合根唯一服务拉起入口 StartDshServiceViaIdentity（按 Identity 直启）"
Assert-True ($shellSrc -notmatch 'StartDshServiceViaVbs') "旧 wscript/vbs 启动链入口名已从组合根根除"

Write-Host "`n== 2.5. Sandbox 静态断言 ==" -ForegroundColor Cyan
# DSH_SANDBOX 门控：四个机器级副作用调用点必须被 DSH_SANDBOX 门控
Assert-True ($shellSrc -match 'IsSandboxMode') "Program.cs 暴露 IsSandboxMode 属性（DSH_SANDBOX=1 判定）"
# 门控检查：IsSandboxMode 必须出现在每个副作用方法的调用路径中
Assert-True ($shellSrc -match 'CleanupProgramDataResidue' -and $shellSrc -match 'IsSandboxMode') "CleanupProgramDataResidue 存在且 IsSandboxMode 存在（门控）"
Assert-True ($shellSrc -match 'EnsureAutoStartRequested' -and $shellSrc -match 'IsSandboxMode') "EnsureAutoStartRequested 存在且 IsSandboxMode 存在（门控）"
Assert-True ($shellSrc -match 'TryPromptOldVersionCleanup' -and $shellSrc -match 'IsSandboxMode') "TryPromptOldVersionCleanup 存在且 IsSandboxMode 存在（门控）"
Assert-True ($shellSrc -match 'CleanupOrphanShortcuts' -and $shellSrc -match 'IsSandboxMode') "CleanupOrphanShortcuts 存在且 IsSandboxMode 存在（门控）"
# 验证门控在正确位置：EnsureSingleInstanceAndAutostart 中有 !IsSandboxMode 保护
Assert-True ($shellSrc -match '!IsSandboxMode') "EnsureSingleInstanceAndAutostart 用 !IsSandboxMode 门控副作用"
# 验证门控在正确位置：【ADR-024】CleanupProgramDataResidue/EnsureAutoStartRequested 实现
# 已迁至 AppEnvironment——方法体沙盒早退门禁改扫 AppEnvironment 源（语义等价不削弱）
$appEnvLines = $appEnvSrc -split "`n"
$inCleanup = $false; $foundGate = $false
for ($i = 0; $i -lt $appEnvLines.Count; $i++) {
    if ($appEnvLines[$i] -match 'internal static void CleanupProgramDataResidue') { $inCleanup = $true }
    if ($inCleanup -and $appEnvLines[$i] -match 'IsSandboxMode.*return') { $foundGate = $true; break }
    if ($inCleanup -and $appEnvLines[$i] -match '^\s*\}') { break }
}
Assert-True $foundGate "CleanupProgramDataResidue 方法体内有 IsSandboxMode 早期返回（AppEnvironment）"
$inEnsure = $false; $foundGate2 = $false
for ($i = 0; $i -lt $appEnvLines.Count; $i++) {
    if ($appEnvLines[$i] -match 'internal static void EnsureAutoStartRequested') { $inEnsure = $true }
    if ($inEnsure -and $appEnvLines[$i] -match 'IsSandboxMode.*return') { $foundGate2 = $true; break }
    if ($inEnsure -and $appEnvLines[$i] -match '^\s*try\s*\{') { break }
}
Assert-True $foundGate2 "EnsureAutoStartRequested 方法体内有 IsSandboxMode 早期返回（AppEnvironment）"
# 组合根仍须保留转发名与门控判定（调用点完整性）
Assert-True ($shellSrc -match 'CleanupProgramDataResidue' -and $shellSrc -match '!IsSandboxMode') "组合根保留 CleanupProgramDataResidue 调用与 !IsSandboxMode 门控"

# DSH_PORTABLE_NODE_DIR 环境覆盖
$runtimeSrc = Get-Content (Join-Path $root "src\DshShell\RuntimeResolver.cs") -Raw
Assert-True ($runtimeSrc -match 'DSH_PORTABLE_NODE_DIR') "RuntimeResolver 支持 DSH_PORTABLE_NODE_DIR 环境覆盖"
Assert-True ($runtimeSrc -match 'DSH_HOME') "RuntimeResolver.RuntimeStatePath 尊重 DSH_HOME 环境变量"

# DSH_NPM_REGISTRY 环境覆盖
$updateSrc = Get-Content (Join-Path $root "src\DshShell\UpdateChecker.cs") -Raw
Assert-True ($updateSrc -match 'DSH_NPM_REGISTRY') "UpdateChecker 支持 DSH_NPM_REGISTRY 环境覆盖"
Assert-True ($updateSrc -match 'NpmRegistryBase') "UpdateChecker 通过 NpmRegistryBase 属性统一 registry 基址"

# 硬编码绝对路径/URL 扫描（新增断言：直接 CI 标红）
$hardcodedViolations = @()
foreach ($f in $srcCsFiles) {
    $lines = Get-Content $f.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].Trim()
        if ($line.StartsWith('//') -or $line.StartsWith('///')) { continue }
        # 检查 registry.npmjs.org 硬编码（排除注释和字符串描述）
        if ($line -match 'registry\.npmjs\.org' -and $f.Name -ne "UpdateChecker.cs") {
            $hardcodedViolations += "$($f.Name):$($i+1): hardcoded registry.npmjs.org: $line"
        }
    }
}
Assert-True ($hardcodedViolations.Count -eq 0) "无 registry.npmjs.org 硬编码（$(if($hardcodedViolations.Count -gt 0){$hardcodedViolations[0]}else{'clean'})"

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
