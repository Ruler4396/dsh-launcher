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

加 -SkipRealOs（CI 快线用，本地不要加）：
  dotnet test 排除 Category=RealOS 层。默认全量——本地跑本脚本仍是真实进程/真实 npm 硬门禁。

加 -RealOsOnly（CI 的 real-os 专用环节用）：
  只跑 Real-OS 层（filter 见 $realOsLayerFilter）+ 1b 节分层归属闸，然后退出。
  与 -SkipRealOs 互斥。两个 filter 字符串的唯一真相源在本文件，workflow 不再各抄一份。

.EXAMPLE
./scripts/test.ps1
./scripts/test.ps1 -Smoke
./scripts/test.ps1 -SkipRealOs   # 只跑快线，Real-OS 层由 CI 的 real-os 专用环节负责
#>
param(
    [switch]$Smoke,
    [switch]$RealNet,
    [switch]$SkipRealOs,
    [switch]$RealOsOnly
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
                        'DSH_TEST_FAKE_APPLY','DSH_TEST_INSTANCE','DSH_PROFILE',
                        'DSH_TEST_NOTICE_CARD','DSH_TEST_TOAST','DSH_TEST_INSTALL_MODE') {
    if (Test-Path "Env:$hygieneVar") {
        Write-Host ("  [clean] " + $hygieneVar)
        Remove-Item "Env:$hygieneVar" -ErrorAction SilentlyContinue
    }
}

Write-Host "== 1. C# 单元测试 (dotnet test) ==" -ForegroundColor Cyan
# 任务二硬门禁：DSH_FORCE_NPM_SMOKE=1 强制 RealWorldNpmExecutionTests 真实执行
#（无 Mock 直接跑 node.exe + npm-cli.js）。本机若无 Node 环境该测试将**失败**并阻断，
# 打破"测试幻觉"——本地验证真实 npm 链路必须可用。注意：这一行是**无条件**设置的，
# CI 上由 build.yml 调起本脚本时同样带这个变量，所以不存在"CI 未设变量→自动跳过"这条路径。
$env:DSH_FORCE_NPM_SMOKE = "1"
# -RealNet：显式开启重型真实网络全链路用例（DshUpdatePipelineRealTests，分钟级、依赖镜像可达性）。
# 默认关闭——CI build 流水线总是调用本脚本，若默认开启会把发布门禁劫持给外部网络状况。
if ($RealNet) { $env:DSH_FORCE_REALNET = "1" } else { Remove-Item Env:DSH_FORCE_REALNET -ErrorAction SilentlyContinue }
# -SkipRealOs：把 Category=RealOS 这一层从本次运行里排除。**默认不开**——本地开发者跑
# test.ps1 仍然是全量硬门禁（真实 npm / 真实进程 / 真实退出码），保住"任务二"那条铁律。
# 只有 CI 传它：那里 Real-OS 层由 -RealOsOnly 那一遍跑（同一份 Release 产物，不再编译一次）。
# 旧写法是两层各跑一次无/半 filter，同 46 条真实用例每次 push 重复一遍（占快线 68s 里的 54s）。
# ---- 分层 filter 的唯一真相源 ----
# 1b 节的不重不漏断言复用下面这两个变量：改这里等于同时改 CI 与门禁，
# 不允许 workflow 里再抄第三份 filter 字符串（抄一份就漂移一份）。
$fastFilter = 'Category!=RealOS'
$realOsLayerFilter = '(Category=RealOS|FullyQualifiedName~RealWorldNpmExecutionTests)&Category!=RealNet'
if ($SkipRealOs -and $RealOsOnly) {
    Write-Host "[FAIL] -SkipRealOs 与 -RealOsOnly 互斥：一次调用只允许跑一层" -ForegroundColor Red
    exit 1
}
$testFilter = if ($RealOsOnly) { $realOsLayerFilter } elseif ($SkipRealOs) { $fastFilter } else { $null }
$testArgs = @((Join-Path $root "tests\DshShell.Tests"), '-c', 'Release', '--nologo', '-v', 'minimal')
if ($testFilter) { $testArgs += @('--filter', $testFilter) }
$testOut = dotnet test $testArgs 2>&1
$testCode = $LASTEXITCODE
# 失败时把断言详情也留下：原来 -v q + 只取最后 12 行，xUnit 的 Error Message 块被整体截掉，
# CI 红了只能靠读代码猜原因（issue #25 排查时踩过）。改 -v minimal + 120 行留证。
$testOut | Select-Object -Last 120
$layerNote = if ($RealOsOnly) { "Real-OS 层" } elseif ($SkipRealOs) { "快线，已排除 Category=RealOS" } else { "全量，含 RealOS 真实环境冒烟" }
Assert-True ($testCode -eq 0) "dotnet test 通过（$layerNote）"

Write-Host "`n== 1b. Real-OS 分层归属断言（源码级，零编译成本）==" -ForegroundColor Cyan
# 分层完全靠 xUnit trait 字符串匹配，而它区分大小写、拼错就**静默失效**：本仓库真出现过
# [Trait("category", "real-os")]——RealOS filter 筛不到它，Category!=RealOS 也排除不掉它，
# 于是两条真起 powershell 的用例只躲在"无 filter 全跑"那一遍里。光修一处不算修完，这里钉死。
# 只扫代码行、不扫注释：注释里讲这个事故经过时必然提到错误拼写（同 G6/G9 那条教训）。
$layerFiles = @(
    Get-ChildItem (Join-Path $root "tests\DshShell.Tests\RealOs") -Filter *.cs -Recurse -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $root "tests\DshShell.Tests") -Filter *.RealOs.cs -Recurse -ErrorAction SilentlyContinue
) | Sort-Object -Property FullName -Unique
Assert-True (@($layerFiles).Count -ge 1) "分层归属闸至少扫到 1 个 Real-OS 文件（实测 $(@($layerFiles).Count)；为 0 = 本闸已经瞎了）"
$misattributed = @()
foreach ($lf in $layerFiles) {
    $lines = @((Get-Content $lf.FullName) | Where-Object { $_ -notmatch '^\s*//' })
    $factIdx = @(); $traitIdx = @(); $badTraitIdx = @(); $classIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]
        if ($ln -match '^\s*\[(Fact|Theory)\b') { $factIdx += $i; continue }
        if ($ln -match '^\s*\[Trait\(') {
            if ($ln -match '^\s*\[Trait\(\s*"Category"\s*,\s*"(RealOS|RealNet)"\s*\)\s*\]') { $traitIdx += $i }
            else { $badTraitIdx += $i }
            continue
        }
        if ($classIdx -lt 0 -and $ln -match '^\s*(public|internal)\s+(sealed\s+|abstract\s+|partial\s+)*class\s') { $classIdx = $i }
    }
    # 类级 trait（写在 class 声明之前）一次覆盖全类方法；否则每个 [Fact]/[Theory] 后面必须紧跟自己的 trait。
    $classLevel = ($classIdx -gt 0 -and @($traitIdx | Where-Object { $_ -lt $classIdx }).Count -gt 0)
    $uncovered = 0
    if (-not $classLevel) {
        foreach ($fi in $factIdx) {
            $hasOwn = $false
            foreach ($ti in $traitIdx) { if ($ti -gt $fi -and $ti -le ($fi + 4)) { $hasOwn = $true; break } }
            if (-not $hasOwn) { $uncovered++ }
        }
    }
    if ($badTraitIdx.Count -gt 0) {
        $misattributed += "$($lf.Name): 第 $($badTraitIdx[0]+1) 行起有 $($badTraitIdx.Count) 处 Trait 不是逐字 [Trait(`"Category`", `"RealOS`")]（大小写/键名不符即筛不到）"
    }
    if ($uncovered -gt 0) {
        $misattributed += "$($lf.Name): $($uncovered)/$($factIdx.Count) 个用例没有 Category trait，会在两层的 filter 里各跑一次或一次都不跑"
    }
}
Assert-True ($misattributed.Count -eq 0) ("Real-OS 层文件的每个用例都必须显式归属（详见上一条注释）；命中：" + ($misattributed -join ' || '))

if ($RealOsOnly) {
    # real-os 专用一遍：静态断言与 uninstall 行为测试属于快线，这里不重复跑。
    Write-Host ""
    if ($script:failed -eq 0) { Write-Host "Real-OS 层通过" -ForegroundColor Green; exit 0 }
    Write-Host "$($script:failed) 项测试失败" -ForegroundColor Red
    exit 1
}

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
$dshUpdateMgrSrc = Get-Content (Join-Path $root "src\DshShell\Managers\DshUpdateManager.cs") -Raw
# 暂存构建事务的所有者（Phase 4 · T2 自 Program.cs 迁入）。下面这些"位置敏感"的不变式
# 一律钉到**具体文件**，不用并集——并集会把位置约束降级成存在约束（用户 2026-09-19 定）。
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
Assert-True ($dshUpdateMgrSrc -match 'StagedUpdate\.MarkPending') "壳实现 dsh 延迟应用更新（staged；T2 后暂存写入位于 DshUpdateManager）"
# 真机实证（2026-09-20 用户指出）：DSH_TEST_UPDATE_SIGNAL 的 dsh 分支曾**直接 return 一个
# 通知结论**，于是绕过"本地已是最新就不提示"这道门，弹出过一张自相矛盾的卡片
# "检测到 dsh 0.1.5-rc.2（当前 0.1.5-rc.2）"。假信号只许替换"远端版本"这一个输入。
$noticeCtors = ([regex]::Matches($dshUpdateMgrSrc, 'new ShellLogic\.UpdateNoticeFlowPolicy\.Outcome\(')).Count
Assert-True ($noticeCtors -eq 1) "DshUpdateManager 只允许 1 处直接构造通知结论（当前 $noticeCtors，只应是 test-hook-ignored）：其余一律经 Decide，否则假信号会绕过已最新不提示的门"
$decideCalls = ([regex]::Matches($dshUpdateMgrSrc, 'UpdateNoticeFlowPolicy\.Decide\s*\(')).Count
Assert-True ($decideCalls -eq 1) "通知裁决必须只有一个入口 UpdateNoticeFlowPolicy.Decide（当前 $decideCalls）"
# 真机实测（2026-09-20）：Phase 4 抽函数把"用户没跳过过更新"编码成比较结果 -1，而判据是
# `<= 0` 即静默 → **全员收不到 dsh 更新提示**（沙盒里没有 skipped-update.json，日志却写着
# skipped-by-user）。跳过状态必须以"版本串或 null"进裁决，不得以结论 int 进。
Assert-True ($dshUpdateMgrSrc -match 'Decide\([^;]*skippedVersion:\s*StagedUpdate\.ReadSkippedDshVersion') "跳过版本必须以版本串（无记录=null）传进 Decide，不得传比较结果 int——哨兵与判据撞车会让'从没跳过过'的用户被当成'已跳过'"
# 真机 T14 实测缺口（修 G）：坏插件在**就绪前**把服务打死时，过去只有 E2010 一条死路——
# 安全模式询问只挂在运行期 E2007 / 页面 E1008 上。启动失败处理必须过这道归因判定。
Assert-True ($shellSrc -match 'StartupFailureRecoveryPolicy\.ShouldOfferSafeMode') "启动失败处理必须咨询 StartupFailureRecoveryPolicy（就绪前插件崩溃也要给安全模式入口，否则用户只剩一个'知道了'）"
# 真机复测（2026-09-20，用户真实 ~/.dsh 注入坏插件）：答"是"之后旧实现只弹一句
# "关闭本提示后重新打开 dsh-launcher"的回执就结束进程——把唯一走得通的那一步丢给用户手动做。
# 现在答"是"必须就地重跑启动流水线（且一次会话只问一次，不可能来回弹）。
Assert-True ($shellSrc -match 'StartupStep\.RetryInSafeMode') "启动失败答复用安全模式后必须就地重跑流水线（RetryInSafeMode），不得只留一句'请你自己重开'"
# 就绪失败的错误码只有一个真相源（真机实测：LauncherApp 把日志码写死成 E2002，弹窗却按裁决给
# E2010——同一件事两个码，按码归因的人只会看到"启动超时"，错过"进程就绪前退出"这个真实形态）
$launcherAppSrc = Get-Content (Join-Path $root "src\DshShell\LauncherApp.cs") -Raw
Assert-True ($launcherAppSrc -notmatch 'Logger\.Error\(\$"[^"]*service readiness failed[^;]*ErrorCodes\.E20') "readiness failed 的日志码不得硬编码错误码"
Assert-True ($launcherAppSrc -match 'service readiness failed[\s\S]{0,160}MapVerdictErrorCode') "readiness failed 日志码必须来自 MapVerdictErrorCode（与用户弹窗同源）"
Assert-True ($shellSrc -notmatch '\.dsh-web\.log') "壳不再引用旧式 .dsh-web.log 路径"

# ---- issue #25 收口：通知只有一条通道（自绘卡片），WPN/系统 Toast 通路整体移除 ----
# 崩溃是 wpnapps.dll 内的 native AV（0xc0000005），托管层拦不住且发生在 Show() 返回之后，
# 所以"加开关"不算修好——只要通路还在，被越过就复发。
# 只拦**代码引用**（成员访问/类型名/DllImport），注释里讲事故经过保留 wpnapps 字样是有
# 搜索价值的线索，不该被判违规。
$srcAll = @(Get-ChildItem (Join-Path $root "src\DshShell") -Recurse -Filter *.cs |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$wpnHits = @($srcAll | Where-Object {
    (Get-Content $_.FullName -Raw) -match 'SystemToast\.|Windows\.UI\.Notifications|CreateToastNotifier|ToastNotificationManager|DllImport\("[^"]*wpnapps' } |
    ForEach-Object { $_.Name })
Assert-True ($wpnHits.Count -eq 0) "壳源码不得再引用 WPN/系统 Toast 通路（命中：$($wpnHits -join ', ')）"
# 真机复测（2026-09-20 用户真实 ~/.dsh 注入坏插件）：答"是"之后旧实现只弹一句
# "关闭本提示后重新打开 dsh-launcher"的回执就结束进程——把唯一走得通的那一步丢给用户手动做。
# 现在回执弹窗与那句承诺都不许存在（放在 $srcAll 定义之后：第一次插错位置把整个脚本跑断了）。
$receiptHits = @($srcAll | Where-Object {
    (Get-Content $_.FullName -Raw) -match 'ArmedNotice|关闭本提示后重新打开' } | ForEach-Object { $_.Name })
Assert-True ($receiptHits.Count -eq 0) "安全模式回执弹窗与'让用户手动重开'的承诺不得存在（命中：$($receiptHits -join ', ')）"
# 真机反馈（同日，用户第二次误判）：点"退出安全模式"后 20+ 秒界面仍挂着已断连的旧页面，
# 看起来就是"没反应"。修复是把这段时间换成壳自绘的等待态（HTML 由纯函数转义产出）。
$safeLifecycleSrc = Get-Content (Join-Path $root "src\DshShell\Lifecycle\SafeModeLifecycle.cs") -Raw
Assert-True ($shellSrc -match 'ShowWaitingPage\("正在退出安全模式' -or ($shellSrc -match 'ShowWaitingPage\(' -and $shellSrc -match 'ExitingSafeModeTitle')) "退出安全模式必须立刻给出可见反馈（等待态 + 标题栏），不能把 20 秒空窗留给用户猜"
$navToString = @($srcAll | Where-Object { (Get-Content $_.FullName -Raw) -match 'NavigateToString\(' } | ForEach-Object { $_.Name })
Assert-True ($navToString.Count -eq 1) "等待态导航只允许一个实现点（命中：$($navToString -join ', ')）——多处 NavigateToString 必然有一份不管 UI 线程/转义"
Assert-True ($safeLifecycleSrc -notmatch 'CoreWebView2|NavigateToString') "Lifecycle 层不得直接碰 WebView2：等待态必须由组合根注入委托（CoreWebView2 是 UI 线程亲和对象）"
$titleWrites = ([regex]::Matches($safeLifecycleSrc, 'form\.Text\s*=')).Count
Assert-True ($titleWrites -eq 1) "安全模式标题栏文字只有一个写入点 ApplyTitle（实测 $titleWrites）——多处写 Text 就是'一处改一处漏'的成因"
Assert-True (Test-Path (Join-Path $root "src\DshShell\Windows\NoticeCard.cs")) "唯一通知实现 NoticeCard.cs 必须存在"
$noticeCardSrc = Get-Content (Join-Path $root "src\DshShell\Windows\NoticeCard.cs") -Raw
Assert-True ($noticeCardSrc -match 'ShowWithoutActivation') "通知卡片非模态：显示时不抢焦点"
Assert-True ($noticeCardSrc -match 'ShellLogic\.NoticeCardLayout') "通知卡片几何只消费纯函数（不得自乘 DPI 系数）"
# 真机反馈（2026-09-20 用户实拍）：圆角难看 + 左侧红/蓝条"没对齐"、左缘有时一条白线。
# 同一个根因链：Region 裁圆角把色条上下两头切掉；OnPaint 先画色条后画 1px 边框，边框压在色条那一列。
# 只扫代码行——整文件正则会被"解释为什么不再有圆角"的注释命中（本闸首跑就被自己的注释判红，
# 与 G6/G9 同一条教训）。
$noticeCardCode = @(Get-Content (Join-Path $root "src\DshShell\Windows\NoticeCard.cs") |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('//') -and -not $_.StartsWith('///') -and -not $_.StartsWith('*') }) -join "`n"
Assert-True ($noticeCardCode -notmatch 'GraphicsPath|Region\s*=\s*new Region') "通知卡片必须直角：不得再用 GraphicsPath/Region 裁角（会裁掉左侧强调条的上下两头）"
$ncBorderAt = $noticeCardCode.IndexOf('DrawRectangle(border')
$ncAccentAt = $noticeCardCode.IndexOf('FillRectangle(accentBrush')
Assert-True ($ncBorderAt -ge 0 -and $ncAccentAt -gt $ncBorderAt) "强调条必须画在边框**之后**（边框压在色条列上=用户看到的左缘白线；当前 border@$ncBorderAt accent@$ncAccentAt）"
# 真机反馈（同一批）：整张卡都是动作热区 → 想复制正文就误触发"退出安全模式并重启"；
# 命中判定必须走纯函数 HitTest，不得再手写"非 × 即整卡触发"。
Assert-True ($noticeCardCode -match 'NoticeCardLayout\.HitTest') "卡片点击必须走 NoticeCardLayout.HitTest（只有 × 与动作行可点）"
Assert-True ($noticeCardCode -notmatch 'CloseRect\.Contains\(e\.Location\)') "卡片不得再手写命中判定（旧实现：不是 × 的地方全都触发动作）"
Assert-True ($noticeCardSrc -match 'NoticeDedupe\.ShouldSuppress') "通知对象必须过去重闸门（保证不重复提示）"
$traySrc = Get-Content (Join-Path $root "src\DshShell\Managers\WindowManager.cs") -Raw
Assert-True ($traySrc -notmatch 'ShowBalloonTip') "托盘气泡不再是通知通道（避免第二套呈现实现回潮）"

# ---- DPI 纪律：自绘窗口的几何只许来自纯函数，且一律物理像素 ----
# 事故链：#28-3（字号按 Point 又被 DC 折算 → s²）、#25 卡片（MonitorDpi 取到面板物理角 DPI
# → 整窗缩一档）、版本信息窗（硬编码 96dpi 像素 + 无 OnDpiChanged → 缩放屏叠列）、
# 托盘菜单（逻辑工作区钳物理坐标 → 菜单离图标越来越远）。四起同一个形状：**单位/来源不同源**。
$verDlgSrc = Get-Content (Join-Path $root "src\DshShell\Windows\VersionInfoDialog.cs") -Raw
Assert-True ($verDlgSrc -match 'VersionDialogLayout\.Compute') "版本信息窗版式必须来自纯函数（不得再写死 96dpi 像素列位）"
Assert-True ($verDlgSrc -match 'OnDpiChanged') "版本信息窗必须响应 DPI 变化整体重排（跨屏/改倍率）"
Assert-True ($traySrc -match 'MonitorWorkArea\.ForPoint') "托盘菜单钳位必须用物理像素工作区 rcWork，不是逻辑 Screen.WorkingArea"
Assert-True ($traySrc -match 'TrayMenuLayout\.PlaceAtCursor') "托盘菜单落点必须是纯函数（贴边偏移随 DPI 折算一次）"
$ctbSrc = Get-Content (Join-Path $root "src\DshShell\Chrome\CustomTitleBar.cs") -Raw
Assert-True ($ctbSrc -notmatch 'new Font\("[^"]+",\s*\d+(\.\d+)?F') "自绘标题栏不得用 Point 单位字号（s² 根因，见 issue #28-3）"
# 真机 T12 实测：单屏 96 DPI 下双击标题栏**从不**最大化（P1 zoomed=False），最大化键却正常。
# 根因就是 OnMouseDown 无条件进系统 HTCAPTION 拖拽模态循环，吞掉第二次点击。这条闸把
# "拖拽必须过阈值"钉成机器规则：接管点唯一，且必须走 ShouldStartCaptionDrag。
Assert-True ($ctbSrc -match 'ShouldStartCaptionDrag') "标题栏拖拽必须经 WindowGeometry.ShouldStartCaptionDrag 阈值判定（否则双击最大化再次变成死代码）"
$ncLButtonDown = ([regex]::Matches($ctbSrc, 'SendMessage\s*\([^;]*Win32Constants\.WM_NCLBUTTONDOWN')).Count
Assert-True ($ncLButtonDown -eq 1) "标题栏只允许 1 处 SendMessage(WM_NCLBUTTONDOWN) 接管点（当前 $ncLButtonDown）：多一处就多一条绕过阈值、吞掉双击的路径"
Assert-True ($noticeCardSrc -match 'Win32DisplayMetricsProvider') "通知卡片的定位与 DPI 必须同源，且取物理像素工作区"
# 真机 T11 实测：主窗拖到 175% 副屏后物理尺寸不变（1280x840），标题栏却已长到 56px——
# 页面可用区被静默压掉 43%。根因是"跨屏后窗口尺寸要跟着倍率走"这条规则**根本没人实现**，
# 而 DPI 几何在组合根被主窗/弹窗各抄一份。现收进 DshShellForm.OnDpiChanged 单一所有者。
$dshFormSrc = Get-Content (Join-Path $root "src\DshShell\Windows\DshShellForm.cs") -Raw
Assert-True ($dshFormSrc -match 'RescaleWindowForDpi') "窗体 DPI 变化必须经 WindowGeometry.RescaleWindowForDpi 跟随新倍率改物理尺寸"
$dpiHandlers = ([regex]::Matches($shellSrc, '\.DpiChanged\s*\+=')).Count
Assert-True ($dpiHandlers -eq 0) "组合根不得再挂 DpiChanged 处理器（当前 $dpiHandlers 处）：几何重算的唯一所有者是 DshShellForm.OnDpiChanged"

# ---- issue #28-4 同族缺口：粘滞安全模式 → 拉起身份，只允许一个 ensure 入口 ----
# 真机端到端实测到的第二次事故：ensure（缺 .dsh-safe 先重建、重建失败退回正常模式）只补在重启
# 路径 StartDshServiceViaIdentity，初始启动的装饰钩子仍裸调 Decorate → dsh 硬失败
# "profile .dsh-safe does not exist" → exit 1 → E2002 service-exited，用户连界面都进不去，
# 更点不到"退出安全模式"。多一个 Decorate 调用点 = 多一条会漏 ensure 的拉起路径。
$decorateCalls = ([regex]::Matches($shellSrc, 'SafeModeLaunchPolicy\.Decorate\s*\(')).Count
Assert-True ($decorateCalls -eq 1) "profile 装饰只允许 1 处调用点（当前 $decorateCalls）：必须收在 EnsureSafeProfileIdentity 内"
Assert-True ($shellSrc -match 'ServiceIdentityDecorator\s*=\s*EnsureSafeProfileIdentity') "初始启动的身份钩子必须等于重启路径同一个 profile ensure 入口（两侧对称）"

# 真机 T9 实测（托盘驻留模式）：真点托盘菜单"退出"后 `host exited=True; service port closed=False`
# ——壳走了、node 还占着端口，下次启动被判僵尸/误杀。决策函数的 Tray 分支必须吃到 TrayExitRequested
# 才成立；组合根漏传这个参数，ServiceLifetime.Tray 注释里"退出才停服务"的承诺就再次静默失效。
Assert-True ($shellSrc -match 'ShouldStopServiceOnClose\([^;]*TrayExitRequested') "退出决策必须把 WindowManager.Instance.TrayExitRequested 传进 ShouldStopServiceOnClose（漏传=托盘退出不服务，真机 T9 实测过的缺陷）"

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
# [臃肿审计 Phase 6] 反向断言。旧版本这里写的是 `-match 'GetAncestorPids'`——祖先链杀伤早在
# 2026-08 被停用（ServiceManager._ancestors 只写不读、生产与测试路径都不执行它），而这条断言
# 正在**文字上保护死码**：删掉它反而会 CI 红。现在要求整套死链（收集器 + Toolhelp32 快照 +
# P/Invoke）确实不在代码里。
Assert-True ($logicSrc -notmatch 'GetAncestorPids|SnapshotParentPids|CreateToolhelp32Snapshot') "壳不再收集/杀伤祖先进程（Toolhelp32 死链已根除）"
# Phase 4 · T2：暂存构建事务整体迁入 DshUpdateManager，故构建类不变式改扫 $updateCoreSrc
# （= Program.cs + DshUpdateManager.cs）。按 2026-09-19 的决定，T2 迁移涉及的不变式一律
# 改用上面的 $dshUpdateMgrSrc 定向断言，不用并集——避免把"位置约束"降级成"存在约束"。
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
Assert-True ($dshUpdateMgrSrc -match 'runtime-build-') "下载管线在隔离 staging buildDir 构建运行时（不污染生产 runtimes）"
Assert-True ($dshUpdateMgrSrc -match 'TryDeleteDir\(buildDir\)') "每次构建前强制清场 buildDir（防残留 lockfile 导致 pnpm 假成功）"
Assert-True ($dshUpdateMgrSrc -match 'pointing at buildDir being rebuilt') "重建前清掉指向本 buildDir 的 stale pending（防半成品被强制应用）"
Assert-True ($updateCoreSrc -match '--prefix') "npm 回退安装走 --prefix 局部树（不触碰全局环境）"
Assert-True ($shellSrc -match '--no-audit --no-fund') "安装统一 --no-audit --no-fund（跳过审计/fund，加速安装）"
Assert-True ($shellSrc -match 'GetNpmRegistrySources') "pack/build/apply 共用同一源序列 GetNpmRegistrySources（防跨 registry cache miss）"
Assert-True ($dshUpdateMgrSrc -match 'preserving tarball for next launch retry') "构建失败保留 tarball 待下次重试（降级不断链路）"
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
# 通知裁决的入参形状：只收版本串，不收"比较结果 int"。int 哨兵（-1=无记录）与判据
# （<=0=静默）撞车过一次，代价是全员收不到更新提示——签名层面就不给它复发的位置。
$decideSig = [regex]::Match($logicSrc, 'public static Outcome Decide\([^)]*\)')
Assert-True ($decideSig.Success) "找不到 UpdateNoticeFlowPolicy.Decide 签名（测量器退化，先查纯函数是否被改名）"
Assert-True ($decideSig.Value -notmatch '\bint\b') "Decide 不得再收 int 型比较结果入参（实测签名：$($decideSig.Value -replace '\s+',' ')）"
# ---- 预热工作目录修复断言随架构升级改锁新形态：npm 回退构建必须显式传 buildDir 工作目录
#      （相对路径 ./<tarball> 依赖该目录；ENOENT 根因同类，工作域从 prefetch_temp 迁移到 buildDir）
#      【ADR-024】实现现居 ProcessRunner.RunNpmCommand ----
Assert-True ($engineSrc -match 'WorkingDirectory = workingDirectory') "RunNpmCommand 支持工作目录参数（相对路径 ./<tarball> 依赖该目录；现居 ProcessRunner）"
Assert-True ($updateCoreSrc -match 'workingDirectory: buildDir') "npm 构建传入 staging buildDir 为工作目录（相对路径 ./<tarball> 依赖该目录；内核现居 DshUpdateManager）"
# ---- 下载秒败"文件名、目录名或卷标语法不正确"根因修复断言随架构升级改锁新形态：
#      pack 目标目录先创建（buildDir 由 Directory.CreateDirectory 保证存在）----
Assert-True ($dshUpdateMgrSrc -match 'Directory\.CreateDirectory\(buildDir\)') "pack 前先创建目标构建目录（历史 ERROR_INVALID_NAME 场景的等价修复）"
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

# ================== 【ADR-024 补口】体量与依赖方向棘轮 ==================
# 为什么需要这一节（2026-09-19 审计，证据可复核）：
#   本脚本历史上**从未有过任何文件行数断言**——`git log --all -S 'Count -le' -- scripts/test.ps1`
#   返回空。上方 2.2 节禁的是 9 个业务**原语 token**，它今天全绿。也就是说 Program.cs 被禁止
#   "拿铲子"，却完全不受限制地"决定往哪挖"：78ef4b3e（2026-08-28 重构收官）的 2975 行，
#   22 天后回涨到 3870（+880，+30%），而 ADR-024 自己在 ARCHITECTURE_DECISIONS.md 里写下的
#   量化指标是「组合根瘦身 ~4000→~2800 行」。**唯一有效的规则一直是散文。**
#   本节把三类只能靠人自觉的不变量（体量、单方法长度、依赖方向）变成机器规则。
#
# 棘轮（ratchet）语义，三条硬约定：
#   1) 基线 = 钉死"当下实测值"，余量 +0。加一行代码必须先删一行。
#   2) **只降不升**：调高任何基线数字 = 削弱门禁 = 违反 AGENTS.md 的 test.ps1 铁律。
#   3) 代码变少后必须把基线同步改小，否则锁不住下一次回涨。
# 数"代码行"而非总行数：否则堆注释即可绕开闸。
Write-Host "`n== 2.3 体量与依赖方向棘轮（ADR-024 补口；只降不升） ==" -ForegroundColor Cyan
function Get-CodeLineCount {
    param([string[]]$Lines)
    $n = 0
    foreach ($l in $Lines) {
        $t = $l.Trim()
        if ($t -eq '') { continue }
        if ($t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('*')) { continue }
        $n++
    }
    return $n
}

# ---- G1 组合根体量：只闸 Program.cs，不闸 ShellLogic.cs ----
# 审计实测 ShellLogic 同期 +887 行属**合法增长**（NoticeCardLayout/SplashLayout/TrayMenuLayout
# 等带契约测试的纯函数）。给它加行数闸只会把人流向更糟的地方；ShellLogic 真正的风险是"往纯
# 函数文件里塞不纯的东西"，那由 G1b 按原语计数锁住。
$g1ProgramCode = Get-CodeLineCount -Lines $programLines
# 2014 → 2007：回滚事务补"先停服再隔离"那一环时组合根多了一处注入（+3），同批把更新应用后的
# 第四份手写"投递到 UI 线程再导航"收敛到 PostNavigateToServiceUrl（-10）——净降，基线随之钉低。
# 2007 → 2006：真机 T14 的安全模式入口最初在组合根加了 24 行代码行，本闸把它拦红；出路不是抬基线，
# 是把"建 profile → 置标志"这条事务交回 Domain（SafeModeLaunchPolicy.ArmNextLaunch）、把两句文案交回
# ShellLogic 策略——净降 1 行，基线按规则三只许同步变小。
# 2006 → 2003：真机用户实环境复测又抓到"答完是只弹回执、要用户自己重开"（见落点 10 后续），改成
# 就地重跑流水线时把失败正文整段下沉成 ShellLogic 纯函数（StartupFailureBody，7 例契约），组合根净降 3 行。
# 2003 → 2001：加"等待态"这条可见反馈时，导航原语整个交回 WebViewManager（NavigateMainWeb /
# ShowWaitingPage，NavigateToString 全仓唯一），组合根只留"投递到 UI 线程"——顺手把此前抄在
# 组合根的第二份 Navigate+try/catch 也收掉，净降 2 行。
Assert-True ($g1ProgramCode -le 2001) "【棘轮 G1】Program.cs 代码行数 ≤ 2001（实测 $g1ProgramCode）"

# ---- G1b 纯函数文件的不纯原语计数（新增即红）----
# ShellLogic.cs 自称纯函数文件（文件头规则要求有生命周期状态的资源必须抽走），但已实测驻留
# 12 处原语。其中 `UpdateProxyPolicy.LocalProxyAlive` 开真 TcpClient 且**无契约测试**，是重构
# 后新增的叶子——正是本闸要拦的形状。基线随搬迁下移：12（Phase 1 实测）→ 11（D2 把第二份
# ProcessStartInfo/Process.Start 并进 RunTaskKill），只许减少。
# 用 -cmatch：`-match` 不区分大小写，会把 URL "registry.npmmirror.com" 误判成 Registry. API。
$logicLines = @(Get-Content (Join-Path $root "src\DshShell\ShellLogic.cs"))
$impurePatterns = @(
    'System\.Net\.Sockets\.TcpClient|new\s+TcpClient',
    'new\s+System\.Net\.Http\.HttpClient|new\s+HttpClient\b|HttpClient\s*\{',
    'new\s+ProcessStartInfo|Process\.Start\(',
    'File\.Delete\(|Directory\.Delete\(',
    'Microsoft\.Win32\.Registry|\bRegistry\.(GetValue|LocalMachine|CurrentUser|OpenBaseKey)'
)
$impureHits = @()
for ($i = 0; $i -lt $logicLines.Count; $i++) {
    $t = $logicLines[$i].Trim()
    if ($t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('*')) { continue }
    foreach ($p in $impurePatterns) { if ($t -cmatch $p) { $impureHits += "$($i+1)"; break } }
}
Assert-True ($impureHits.Count -le 11) "【棘轮 G1b】ShellLogic.cs 不纯原语行 ≤ 11（实测 $($impureHits.Count)：行 $(if($impureHits.Count){$impureHits[0..([Math]::Min(4,$impureHits.Count-1))] -join ','}else{'clean'})…）"

# ---- G2 单方法长度预算 ----
# 光有总量闸会把人逼成更大的方法（现状：RunUserInterface 一个方法 424 行）。
# 扫描：类体内 4 空格缩进的成员签名 → 向后花括号配平（剥离注释行与字符串字面量内的括号）。
function Get-MaxMethodSpan {
    param([string[]]$Lines)
    # 成员签名固定 4 空格缩进（Program.cs 全文件一致），正则里直接写字面量。
    # ⚠️ 不要改成 param([int]$Pad) + $pad=' '*$Pad 的形式：PowerShell 变量名**大小写不敏感**，
    # $pad 与 $Pad 是同一个变量，且被 param 声明为 [int] 后强转存回字符串 → 静默变成 0，
    # 本函数会返回 Max=0 让闸永远"绿"。（实测踩过，故此处刻意不参数化缩进。）
    $sigRe   = '^    (?:public|private|internal|protected)\b'
    $skipRe  = '^    (?:public|private|internal|protected)\b[^\r\n]*[=;]\s*$'
    $max = 0; $maxName = ''
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $line = $Lines[$i]
        if ($line -notmatch $sigRe) { continue }
        if ($line -match $skipRe -or $line -match '\bconst\b') { continue }
        if ($line -notmatch '\s(\w+)\s*(\(|=>)') { continue }
        $name = $Matches[1]
        $bodyIdx = -1
        for ($j = $i; $j -lt [Math]::Min($i + 6, $Lines.Count); $j++) {
            $t = $Lines[$j].Trim()
            if ($t.EndsWith('{')) { $bodyIdx = $j; break }
            if ($t -eq '}') { break }
        }
        if ($bodyIdx -lt 0) { continue }
        $depth = 0; $end = $bodyIdx
        for ($k = $bodyIdx; $k -lt $Lines.Count; $k++) {
            $t = $Lines[$k].Trim()
            $isComment = $t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('*')
            $scan = if ($isComment) { '' } else { ($Lines[$k] -replace '"[^"]*"', '') -replace "'[^']*'", '' }
            $depth += ([regex]::Matches($scan, '\{')).Count - ([regex]::Matches($scan, '\}')).Count
            if ($depth -le 0) { $end = $k; break }
        }
        $span = $end - $i + 1
        if ($span -gt $max) { $max = $span; $maxName = $name }
    }
    return @{ Max = $max; Name = $maxName }
}
$g2 = Get-MaxMethodSpan -Lines $programLines
# 366 → 300：T6 把 RunUserInterface 里那条 66 行的 FormClosing lambda 拆成三个具名方法
# （托盘降级隐藏 / 构建防误关询问 / 退出编排分派）。光有 G1 的总量闸只会把人逼成更大的方法，
# 本闸就是这个缺口的另一半。
Assert-True ($g2.Max -le 300) "【棘轮 G2】Program.cs 单方法 ≤ 300 行（实测最大 $($g2.Name)=$($g2.Max)）"
# 扫描器自检：若解析退化（如缩进变化/正则失配）会返回 Max=0 而"通过"上面的闸——那是一条假绿。
# 断言它确实找到了一个成体量级的方法，让坏掉的闸变红而不是变绿。
Assert-True ($g2.Max -ge 100 -and $g2.Name) "【G2 自检】方法长度扫描器工作正常（实测 $($g2.Name)=$($g2.Max)，须 ≥100 才说明解析未退化）"

# ---- G3 上向调用冻结清单（Manager/Window/Chrome 不得回调组合根静态）----
# docs/00 铁律：「严禁 Manager 向上回调 Program 的静态方法」。此规则此前零机器检查。
# Phase 4·T7/T8 已把这 16 处全部清零（P/Invoke 与常量归 Win32/、图标缓存归 Windows/WindowIcons、
# Trace 改直调 Logger、弹窗与 per-window 状态改注入委托）。基线因此从「冻结清单」收紧为**硬零**：
# 今后任何一处 Manager/Windows/Chrome/Domain/Lifecycle/Win32 回调 Program 静态，CI 直接红。
$upwardHits = @()
foreach ($d in @('Managers','Windows','Chrome','Domain','Lifecycle','Win32')) {
    $dp = Join-Path $root "src\DshShell\$d"
    if (-not (Test-Path $dp)) { continue }
    foreach ($f in (Get-ChildItem $dp -Recurse -Filter *.cs)) {
        $fl = @(Get-Content $f.FullName)
        for ($i = 0; $i -lt $fl.Count; $i++) {
            $t = $fl[$i].Trim()
            if ($t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('*')) { continue }
            foreach ($mm in [regex]::Matches($t, '(?:DshWeb\.)?Program\s*\.\s*(\w+)')) {
                $upwardHits += "$($f.Name)->$($mm.Groups[1].Value)"
            }
        }
    }
}
Assert-True ($upwardHits.Count -le 0) "【G3 硬闸】下层代码零回调 Program 静态（实测 $($upwardHits.Count)：$(if($upwardHits.Count){$upwardHits[0]}else{'clean'})）"

# ---- G4 Manager 互不引用（docs/00 核心约束：Manager 之间严禁直接引用对方）----
# 审计修正：此前以为只有 TrayManager→WindowManager 一处，实测 11 处——其中
# WindowManager→WebViewManager 达 9 处，且读写的正是映射表禁止做成进程级静态的那三个
# 每窗字段（MainWeb/RecoveryNeeded/HiddenSince）。基线随搬迁下移：11（Phase 1 实测）
# → 9（Phase 6 删除 TrayManager——它那两行就是纯委托空壳），只许减少。
$mgrDir = Join-Path $root "src\DshShell\Managers"
# ProcessRunner/WebRuntimeInstaller/SelftestReporter/ManagerInterfaces 是无状态工具类或契约
# 文件而非对等 Manager，豁免（把 ProcessRunner 当对等 Manager 会虚报 7 处）。
$peerManagers = @(Get-ChildItem $mgrDir -Filter *.cs | ForEach-Object { $_.BaseName } |
                  Where-Object { $_ -notin @('ManagerInterfaces','SelftestReporter','WebRuntimeInstaller','ProcessRunner') })
$siblingHits = @()
foreach ($f in (Get-ChildItem $mgrDir -Filter *.cs)) {
    $fl = @(Get-Content $f.FullName)
    for ($i = 0; $i -lt $fl.Count; $i++) {
        $t = $fl[$i].Trim()
        if ($t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('*')) { continue }
        foreach ($m in $peerManagers) {
            if ($m -eq $f.BaseName) { continue }
            if ($t -cmatch "\b$([regex]::Escape($m))\b") { $siblingHits += "$($f.Name):$m"; break }
        }
    }
}
Assert-True ($siblingHits.Count -le 9) "【棘轮 G4】Manager 互不引用：兄弟引用 ≤ 9 处（实测 $($siblingHits.Count)）"

# ---- G5 组合根静态流程标志冻结 ----
# docs/00 + AGENTS.md 明文：「严禁使用全局 static bool 控流程，状态必须定义在 LifecycleState
# 枚举中」——此前零机器检查。最刺眼的一条：Program.cs 的 _lastShellRestartUtc 上方注释原文写着
# 「时间戳事实，不做流程控制标志（架构铁律：严禁 static bool 控流程）」，然后用 DateTime.MinValue
# 哨兵承载状态。按字段名冻结当下集合，新增一个静态标量字段即红。
# 冻结清单随搬迁缩短：13（Phase 1 起算）→ 12（3c 删 _serviceStartedByShell）
# → 11（T6 迁走 _navSucceededSinceFailure）→ 8（T1 把重启预算三字段连事务迁入 ServiceRestartCoordinator）
# → 7（T6b：构建占用状态连事务迁入 DshUpdateManager.BuildInProgress）。
# 清单里被删掉的名字若回流，本闸立即变红（回流的名字同时进 G9 的禁止清单，双保险）。
$staticFlagAllowed = @(
    'ServerManagedExternally','_servicePid',
    '_applyRestartDeferred','_cachedGlobalDshVersionLoaded',
    '_lastBuildUiPercent','_lastBuildUiApplyTicks','_shutdownInitiated'
)
$staticFlagHits = @()
for ($i = 0; $i -lt $programLines.Count; $i++) {
    $t = $programLines[$i].Trim()
    if ($t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('*')) { continue }
    # 类型后允许 `?`：把 DateTime 改成 DateTime? 不得成为绕开本闸的后门
    if ($t -cmatch '^private\s+static\s+(volatile\s+)?(readonly\s+)?(bool|int|long|DateTime|DateTimeOffset)\??\s+(\w+)\s*(=[^>]|;)') {
        if ($staticFlagAllowed -notcontains $Matches[4]) { $staticFlagHits += "$($Matches[4]):$($i+1)" }
    }
}
Assert-True ($staticFlagHits.Count -eq 0) "【G5】Program.cs 未新增冻结清单外的静态标量流程字段（新增：$(if($staticFlagHits.Count){$staticFlagHits -join ','}else{'无'})）"

# ---- G6 空 catch 上限 ----
# docs/00：「严禁出现空的 catch {}」——此前零机器检查。异常透明性是本项目硬约束（用户要能看到
# 真实失败原因）。基线随 T2 的下沉从 37 降到 35，T5 收官 33，D1 把 6 份手写进程采集并成一份后 31，
# Phase 6 删掉祖先链死码（其 `catch { }` 静默吞异常）后 30（只许减少；数字来源不追究——
# 同工作树另有并行会话，抬高基线才是违规）。
# 必须逐行剥离注释行再匹配：本闸最初用整文件正则，结果把解释性注释里写的字面量
# `<c>catch { }</c>` 也算成违规（把说明缺陷的注释删掉来让闸变绿 = 反向激励）。
# 与 2.2 节同源的限制：行尾注释内的命中由人工评审兜底。
$emptyCatchTotal = 0
foreach ($f in $srcAll) {
    foreach ($cl in (Get-Content $f.FullName)) {
        $t = $cl.Trim()
        if ($t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('*')) { continue }
        $emptyCatchTotal += ([regex]::Matches($cl, 'catch[^\{\r\n]*\{\s*\}')).Count
    }
}
Assert-True ($emptyCatchTotal -le 30) "【棘轮 G6】src/DshShell 空 catch 总数 ≤ 30（实测 $emptyCatchTotal）"

# ---- G7 CHANGELOG 结构闸 ----
# CHANGELOG.md 是 884 行只增文件，而 .github/workflows/build.yml 的发布闸只校验"版本条目存在"
# ——一个只增闸管不住只增文件。实测本窗口内它 +301 行（+58%），并长出**两个** `## [Unreleased]`
# 锚点（一个在文件头，一个是 0.3.5 之前的空孤儿）。锚点数用 -eq 1 钉死，段长走棘轮。
$changelogLines = @(Get-Content (Join-Path $root "CHANGELOG.md"))
$unreleasedAnchors = @()
for ($i = 0; $i -lt $changelogLines.Count; $i++) {
    if ($changelogLines[$i] -match '^## \[Unreleased\]') { $unreleasedAnchors += $i }
}
Assert-True ($unreleasedAnchors.Count -eq 1) "【G7】CHANGELOG 恰好 1 个 [Unreleased] 锚点（实测 $($unreleasedAnchors.Count)）"
$unrelLen = 0
if ($unreleasedAnchors.Count -ge 1) {
    $next = $changelogLines.Count
    for ($i = $unreleasedAnchors[0] + 1; $i -lt $changelogLines.Count; $i++) {
        if ($changelogLines[$i] -match '^## \[') { $next = $i; break }
    }
    $unrelLen = $next - $unreleasedAnchors[0]
}
Assert-True ($unrelLen -le 22) "【G7】CHANGELOG [Unreleased] 段 ≤ 22 行（实测 $unrelLen；定版时必须整段搬进版本标题）"
# 上限沿革（每次都要写清授权来源，否则"抬上限"会悄悄变成默认动作）：
#   315 → 400：用户 2026-09-19 明确授权（当时该段被并行会话的 DPI 批次填到 315/315，任何新记录都红）。
#   400 → 432 → 454：用户 2026-09-20 授权放宽以记本轮 CI 分层整改 + 测试内容审计，两次都按"钉当下
#     实测值、余量 +0"执行；同日也留下台账第 16 条预言过的代价——"每加一条都要先还一行"。
#   454 → 4：**同日 v0.5.0/v0.5.1 定版，整段搬进版本标题**（正是本闸注释一直要求的那个收口动作）。
#     按规则三"代码变少后必须同步改小，否则锁不住下一次回涨"当场收紧，不留在 454 当空门。
#     后果如实记下：现在任何一条新记录都会红，出路只有两条——先定版搬段，或按同等口径显式授权抬数。
#   4 → 22：用户 2026-09-21 在两条出路里显式选了"抬数"（另一条是定版 v0.5.2 搬段，但那会让 MSI/ZIP
#     与 0.5.1 逐字节相同、白发布一次）。入账的是发布链两条修复：build.yml 的 `ref_type` 单复数
#     （v0.5.0/v0.5.1 的 tag run 因此红/靠 dispatch 绕行）+ 发布正文换行归一（scripts/release-notes.ps1
#     成为正文唯一实现）。仍按"钉当下实测值、余量 +0"执行；下一次定版必须整段搬走并当场收回。

# ---- G8 文档↔代码一致性（防"权威文档毒化后续 agent"）----
# 实测到的真实危害：docs/00-ARCHITECTURE-GUARDRAILS-MANDATORY.md 曾**正面命令**"必须使用 cmd.exe /c
# 包装"，而 ADR-021 与 test.ps1 的 ADR-021 闸**严禁** src/ 出现 cmd.exe——AGENTS.md 又要求每个
# agent 先读 docs/00，于是照规则办事的 agent 拿到的第一手答案是错的。78ef4b3e 的 8j 批次只改了
# AGENTS.md，漏掉了它指向的那份。此处把两类失真都变成断言。
$guardrailSrc = Get-Content (Join-Path $root "docs\00-ARCHITECTURE-GUARDRAILS-MANDATORY.md") -Raw
$agentsSrc = Get-Content (Join-Path $root "AGENTS.md") -Raw
# 只拦**正面命令**使用 cmd.exe / npm.cmd 的行；同句出现「严禁/禁止/不得/不应」的是在陈述禁令，
# 必须放行——否则文档连"严禁 cmd.exe /c"都不能写，等于把闸改成不许有注释。
$cmdExeDocViolations = @()
foreach ($gl in ($guardrailSrc -split "`n")) {
    $t = $gl.Trim()
    if ($t -match 'cmd\.exe\s+/c|npm\.cmd') {
        if ($t -notmatch '严禁|禁止|不得|不应|不要|已消灭|历史') { $cmdExeDocViolations += $t }
    }
}
Assert-True ($cmdExeDocViolations.Count -eq 0) "【G8】docs/00 不得正面命令使用 cmd.exe /c 或 npm.cmd（首例：$(if($cmdExeDocViolations.Count){$cmdExeDocViolations[0]}else{'clean'})）"
# AGENTS.md 项目地图必须点名 Managers/ 下每个**对等 Manager**（新增 Manager 而不忘改地图）
$missingManagers = @()
foreach ($mf in (Get-ChildItem $mgrDir -Filter '*Manager.cs')) {
    $mn = $mf.BaseName
    if ($mn -eq 'ManagerInterfaces') { continue }
    if ($agentsSrc -notmatch [regex]::Escape($mn)) { $missingManagers += $mn }
}
Assert-True ($missingManagers.Count -eq 0) "【G8】AGENTS.md 项目地图列全 Managers/ 下每个对等 Manager（缺失：$(if($missingManagers.Count){$missingManagers -join ','}else{'无'})）"
# 映射表必须声明自己的时效——它自称「唯一权威」却已 13/21 行与现状不符
$mappingSrc = Get-Content (Join-Path $root "docs\refactor-static-mapping.md") -Raw
Assert-True ($mappingSrc -notmatch '唯一权威' -or $mappingSrc -match 'FROZEN|历史|已过时') "【G8】refactor-static-mapping.md 不得在未标注时效的情况下自称唯一权威"

# ---- G9 已迁出的运行期事务不得回流组合根（Phase 4 · T1/T3/T5）----
# G1 只闸**总量**：把事务搬走再搬回来，只要行数不超基线就测不出来。本闸闸**位置**。
# 搬迁同时改了名（旧名是"组合根里的一个 static 方法"这一事实的一部分），所以表里两侧都记：
#   负向 = 旧名不得再出现在组合根的**代码行**；正向 = 新名必须活在归属文件里（搬迁被回退即红）。
# 只扫代码行：G6 的同类教训——整文件正则会命中"解释为什么不再有第二份真相"的注释，
# 等于逼着人删掉记录缺陷的注释。首跑就是靠这条抓到本闸自己的两个假红。
function Get-G9CodeOnly {
    param([string[]]$Lines)
    $only = @()
    foreach ($cl in $Lines) {
        $ct = $cl.Trim()
        if (-not $ct -or $ct.StartsWith('//') -or $ct.StartsWith('///') -or $ct.StartsWith('*')) { continue }
        $only += $ct
    }
    return ,$only
}
$g9ProgramCode = Get-G9CodeOnly -Lines $programLines
$g9Moves = @(
    @{ Old='RestartDshServiceCoreAsync';           New='OnServiceExited';       Owner='Lifecycle/ServiceRestartCoordinator.cs' },
    @{ Old='HandleRuntimeServiceExit';             New='OnServiceExited';       Owner='Lifecycle/ServiceRestartCoordinator.cs' },
    @{ Old='TryStartSafeMode';                     New='TryEnter';              Owner='Lifecycle/SafeModeLifecycle.cs' },
    @{ Old='WaitSafeModeVerified';                 New='WaitVerified';          Owner='Lifecycle/SafeModeLifecycle.cs' },
    @{ Old='HandleUpdateRollbackOnBootFailure';    New='TryHandleBootFailure';  Owner='Lifecycle/UpdateRollbackCoordinator.cs' },
    @{ Old='ArmUpdateRollbackGuardFromPersistedState'; New='ArmFromPersistedState'; Owner='Lifecycle/UpdateRollbackCoordinator.cs' },
    @{ Old='HandleUpdateConfirmedHealthy';         New='ConfirmHealthy';        Owner='Lifecycle/UpdateRollbackCoordinator.cs' },
    # 真机 T14（就绪前插件崩溃补安全模式入口）：这条"建 profile → 置标志"的事务先长在组合根
    # 一个新加的 static 方法里，被 G1 棘轮当场拦红（2031 > 2007）。红得对——事务不该留在那儿。
    # 现落 Domain，两侧同时钉住。
    @{ Old='EnableSafeModeForNextLaunch';          New='ArmNextLaunch';         Owner='Domain/SafeModeLaunchPolicy.cs' }
)
$g9Leaks = @()
$g9OwnerCache = @{}
foreach ($mv in $g9Moves) {
    foreach ($gl in $g9ProgramCode) {
        if ($gl -cmatch [regex]::Escape($mv.Old)) { $g9Leaks += "$($mv.Old) 回流组合根 => $gl" }
    }
    if (-not $g9OwnerCache.ContainsKey($mv.Owner)) {
        $g9OwnerCache[$mv.Owner] = Get-G9CodeOnly -Lines @(Get-Content (Join-Path $root "src\DshShell/$($mv.Owner)"))
    }
    if (-not ($g9OwnerCache[$mv.Owner] | Where-Object { $_ -cmatch [regex]::Escape($mv.New) })) {
        $g9Leaks += "$($mv.New) 已不在 $($mv.Owner)（搬迁被回退？）"
    }
}
# 静态流程字段：出现在组合根即违规（G5 只管 bool/int/DateTime，字符串状态与取消源同样是流程控制；
# _isBuildInProgress/_buildCts 是 T6b 迁走的，回流即红）
foreach ($fld in @('_updateRollbackArmedVersion', '_preApplyIdentityVersion', '_runtimeRestartAttempts',
    '_lastRuntimeRestartUtc', '_isBuildInProgress', '_buildCts')) {
    foreach ($gl in $g9ProgramCode) { if ($gl -cmatch [regex]::Escape($fld)) { $g9Leaks += "$fld => $gl" } }
}
Assert-True ($g9Leaks.Count -eq 0) "【G9】已迁出的运行期事务符号未回流组合根（首例：$(if($g9Leaks.Count){$g9Leaks[0]}else{'clean'})）"
# 回滚不得自带第二份就绪轮询：90 秒 Thread.Sleep 阻塞循环是 T5 迁走的东西，现在复用共享重启事务
$rollbackCode = Get-G9CodeOnly -Lines @(Get-Content (Join-Path $root "src\DshShell\Lifecycle\UpdateRollbackCoordinator.cs"))
$g9BlockingPolls = @($rollbackCode | Where-Object { $_ -cmatch 'Thread\.Sleep\(' })
Assert-True ($g9BlockingPolls.Count -eq 0) "【G9】回滚复用 ServiceRestartCoordinator，无手写阻塞轮询（首例：$(if($g9BlockingPolls.Count){$g9BlockingPolls[0]}else{'clean'})）"
$g9BudgetHits = @($rollbackCode | Where-Object { $_ -cmatch 'RollbackReadyBudgetSeconds' })
Assert-True ($g9BudgetHits.Count -ge 1) "【G9】回滚就绪预算在协调器内显式声明（可被测试钉住）"
# 运行期事务的"刷新页面"必须经 UI 投递：协调器都跑在后台线程上，而 CoreWebView2 只允许在
# UI 线程访问。真机回滚演练实测抓到过一次回归——搬迁时把 `TryPostToMainForm(form, Navigate...)`
# 写成了裸委托，回滚**已经成功**却在线程检查上抛异常、被 catch 判成失败（监控被停 + 无结果弹窗）。
# 断言：Program.cs 里每一处 NavigateToServiceUrl 注入都必须是投递版（Post*/lambda），不得裸给方法名。
$g9NavRaw = @($g9ProgramCode | Where-Object {
    $_ -cmatch 'NavigateToServiceUrl:\s*' -and $_ -notmatch 'NavigateToServiceUrl:\s*(Post\w+|\()' })
Assert-True ($g9NavRaw.Count -eq 0) "【G9】导航注入必须走 UI 投递线程（裸委托会抛跨线程异常：$(if($g9NavRaw.Count){$g9NavRaw[0]}else{'clean'})）"

# ---- G10 DPI 换算只许一处实现（Phase 5 · D8）----
# B4 的真实成因不是"某个布局算错"，而是同一条安全不变量（≤0 当 96、系数钳 [0.5,8]）被抄了 8 份，
# 其中一份漏了钳制。收敛后钳制只允许存在于 ShellLogic.DpiScale；再出现第二份钳制字面量 = 又开了
# 一个可以各自漏一条的副本。第二断言统计**未钳制**的裸 `/96f` 换算：坏驱动/RDP 给出
# deviceDpi=0 时它们会算出 0 缩放（标题栏 0px）。原先是棘轮（实测 10 处），Phase 5 · D8 收尾时
# 全部改走 DpiScale，现降为**硬零**：再出现一处裸除法，就是又一次"B4 式"漏钳制在酝酿。
# （`SetResolution(96f, 96f)` 与 `_scale * 96f` 这类点/英寸换算不是 DPI 缩放，模式 `/ 96f` 不误伤。）
$g10ClampSites = @()
$g10RawDivisions = @()
foreach ($gf in $srcAll) {
    $isOwner = $gf.FullName.EndsWith('ShellLogic.cs')
    foreach ($gl0 in (Get-Content $gf.FullName)) {
        $gt = $gl0.Trim()
        if (-not $gt -or $gt.StartsWith('//') -or $gt.StartsWith('///') -or $gt.StartsWith('*')) { continue }
        if ($gt -cmatch 'Math\.Clamp' -and $gt -cmatch '96f|BaseDpi' -and -not $isOwner) {
            $g10ClampSites += "$($gf.Name): $gt"
        }
        if ($gt -cmatch '/\s*96f') { $g10RawDivisions += "$($gf.Name): $gt" }
    }
}
Assert-True ($g10ClampSites.Count -eq 0) "【G10 硬闸】DPI 钳制只在 ShellLogic.DpiScale 一处（第二份：$(if($g10ClampSites.Count){$g10ClampSites[0]}else{'clean'})）"
Assert-True ($g10RawDivisions.Count -eq 0) "【G10 硬闸】无未钳制的裸 /96f 换算（DPI→系数只走 DpiScale.Of；首例：$(if($g10RawDivisions.Count){$g10RawDivisions[0]}else{'clean'})）"

# ---- G11 taskkill 只有一个启动点（Phase 5 · D2）----
# 2026-08 的"等 taskkill 自身退出"竞态修复只打在 RunTaskKill 上，因为另有一份手写 taskkill 启动
# 代码没人记得改。第二份启动点 = 第二份可以各自漏一条腿的实现，一律并到 ShellLogic.RunTaskKill。
$g11TaskKillSites = @()
foreach ($gf in $srcAll) {
    foreach ($gl0 in (Get-Content $gf.FullName)) {
        $gt = $gl0.Trim()
        if ($gt.StartsWith('//') -or $gt.StartsWith('///') -or $gt.StartsWith('*')) { continue }
        if ($gt -cmatch 'ProcessStartInfo\(\s*"?taskkill') { $g11TaskKillSites += "$($gf.Name): $gt" }
    }
}
Assert-True ($g11TaskKillSites.Count -le 1) "【G11】src 内 taskkill 启动点唯一（实测 $($g11TaskKillSites.Count)：$(if($g11TaskKillSites.Count){$g11TaskKillSites[0]}else{'clean'})）"

# ---- G12 进程三必须只许一份实现（Phase 5 · D1）----
# docs/00 的"双流排空 + 限时等待 + 超时 Kill(entireProcessTree)"此前被手工重推导 6 份，
# B1 修的就是其中少了腿的那份（同步 ReadToEnd → 输出超管道缓冲即死锁 + 孤儿进程）。
# 现收敛到 ProcessRunner.RunCapture：调用点数只许增加、自己手写 WaitForExit 的采集点只许减少。
$g12Callers = @(); $g12HandRolled = @()
foreach ($gf in $srcAll) {
    $isOwner = $gf.Name -eq 'ProcessRunner.cs'
    foreach ($gl0 in (Get-Content $gf.FullName)) {
        $gt = $gl0.Trim()
        if ($gt.StartsWith('//') -or $gt.StartsWith('///') -or $gt.StartsWith('*')) { continue }
        if ($gt -cmatch 'ProcessRunner\.RunCapture\s*\(') { $g12Callers += $gf.Name }
        if (-not $isOwner -and $gt -cmatch 'WaitForExit\s*\(\s*\d') { $g12HandRolled += "$($gf.Name): $gt" }
    }
}
Assert-True ($g12Callers.Count -ge 5) "【G12】RunCapture 已是短进程采集主路径（调用点 $($g12Callers.Count)，≥5）"
Assert-True ($g12HandRolled.Count -le 2) "【棘轮 G12】RunCapture 之外手写限时 WaitForExit 的采集点 ≤ 2（实测 $($g12HandRolled.Count)：$(if($g12HandRolled.Count){$g12HandRolled[0]}else{'clean'})）"

# ---- G13 包名/作用域字面量单一真相源（Phase 5 · F9 收尾）----
# `DshDiscovery` 的注释早就写着"路径段与提示文案一律从这里取"，但实测仍有 3 处用户可见文案 +
# 若干 `node_modules/@deepseek-ai/dsh` 路径段是字面量——承诺与现状分叉（scope 变更时任一处漏改，
# 文案就会教用户执行一条装不上当前包的命令）。现在把剩下的都改走常量，并用硬闸钉住。
# 例外（合法）：SafeProfileBuilder 的 `@deepseek-ai/dsh-base` / `-dsh-web-app` 是**不同包名**，
# 但它的 scope 前缀常量本身必须从 PackageScope 派生（第二断言）。
$g13Hits = @()
foreach ($gf in $srcAll) {
    if ($gf.Name -eq 'DshDiscovery.cs') { continue }   # 唯一定义处
    foreach ($gl0 in (Get-Content $gf.FullName)) {
        $gt = $gl0.Trim()
        if (-not $gt -or $gt.StartsWith('//') -or $gt.StartsWith('///') -or $gt.StartsWith('*')) { continue }
        if ($gt -cmatch '@deepseek-ai/dsh(?!-base|-web-app)') { $g13Hits += "$($gf.Name): $gt" }
        if ($gt -cmatch '"node_modules",\s*"@deepseek-ai"') { $g13Hits += "$($gf.Name): $gt" }
        if ($gt -cmatch 'StartsWith\(\s*"@deepseek-ai/"') { $g13Hits += "$($gf.Name): $gt" }
    }
}
Assert-True ($g13Hits.Count -eq 0) "【G13 硬闸】包名/路径段/scope 前缀字面量只从 DshDiscovery 取（首例：$(if($g13Hits.Count){$g13Hits[0]}else{'clean'})）"
$g13ProfileSrc = Get-Content (Join-Path $root "src\DshShell\Domain\SafeProfileBuilder.cs") -Raw
Assert-True ($g13ProfileSrc -notmatch 'DeepSeekScope\s*=\s*"@deepseek-ai/"') "【G13】bundle scope 前缀从 PackageScope 派生，不得另立第二份字面量"

# ---- G14 单实例 mutex 的句柄持有期（2026-09-21 审查 N1 防回归）----
# 修复前的形状：EnsureSingleInstanceAndAutostart 里 `using var mutex = new Mutex(...)`——
# 方法一返回就 Dispose，单实例闸门整个存活期失效（二实例直入完整启动，E1009 永不触发）。
# 两条断言：① new Mutex 全组合根恰一处（自检防退化：句柄若被搬去别处，计数归零同样红）；
# ② 创建它的那一行不得是方法内 using var（持有者必须是 Main 的 using 作用域）。
$g14MutexLines = @($g9ProgramCode | Where-Object { $_ -cmatch 'new\s+Mutex\s*\(' })
Assert-True ($g14MutexLines.Count -eq 1) "【G14】Program.cs 恰有一处 new Mutex（实测 $($g14MutexLines.Count)；多处=双真相源，0 处=句柄搬离组合根需连带改闸）"
$g14BadHold = @($g14MutexLines | Where-Object { $_ -cmatch '^using\s+var\s+\w+\s*=\s*new\s+Mutex' })
Assert-True ($g14BadHold.Count -eq 0) "【G14】单实例 Mutex 不得方法内 using var 创建（随返回释放=闸门失效，审查 N1；首例：$(if($g14BadHold.Count){$g14BadHold[0]}else{'clean'}))"

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
