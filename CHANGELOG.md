# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 修复

- **首装静默失败收口**：第二实例等待主窗从 20s 收紧到 5s，超时不再无声退出而是弹 `[E1009]` 说明；终态崩溃（AppDomain.UnhandledException）在非无头模式弹 `[E9001]` 对话框留线索；首装安装失败以真实根因 `[E1012]` 展示（不再被"缺少 start-dsh.vbs"通用文案掩盖）。
- **启动时延（"点击很久才有窗口"）**：移除 UI 线程上的同步 dsh 身份发现与 Splash 关闭后的重复端口终检/回滚武装阻塞（改为后台线程）；版本探测改异步排空 + 超时杀整树（进程三必须合规），并做会话级记忆化——同一探测不再反复 spawn node。
- **发布链路（Release 更新日志为空）**：tag 提交缺少对应 `## [x.y.z]` CHANGELOG 条目时发布 job 直接 FAIL，禁止占位文案静默上 Release 页（v0.4.0 事故根治）；单测禁用集合间并行，根治 StagedUpdate 静态状态互踩导致的随机红。

### 变更

- **首装（本机无任何 dsh）改为 npm 全局安装** `@deepseek-ai/dsh`：单次安装、复用 npm 缓存，替代 SelfContained staging 双份构建与 npx 冷解析（更快、更省 CPU/内存）；跨镜像源共享总预算（600s，单源上限 420s），每个降级边界向 Splash 发黄色告警；失败响亮停止、绝不静默落 npx。更新引擎的 staging 原子应用链路保持不变。

## [0.4.0] - 2026-08-19

> ## ⚠️ 预览版（Preview）—— 请先阅读
>
> 本版本为 **预览发布（Preview）**，可能包含未知 Bug、行为变更或不稳定表现，**仅供体验与测试，请勿视为正式稳定版本**。
> 若在使用中发现问题，欢迎提交 [Issue](https://github.com/Ruler4396/dsh-launcher/issues)（请附上 `dsh.log` 与简要复现步骤），我们会尽快跟进处理。

> 本版本是一次**大规模架构重构**：将 3000 行的 God Object `Program.cs` 拆分为职责单一的 Manager + 显式状态机，引入极速启动模型，并完成多显示器 DPI 修复。行为应尽量与前版一致，但因改动面广，属预览性质。

### 新增

- **极速启动模型**：启动状态窗（Splash）双缓冲渲染 + `IProgress<T>` 后台流水线回填进度，双击后 <500ms 出现启动窗；取消按钮只撤销后台流程、不放弃已在后台进行中的服务下载/启动；`DSH_TEST_SPLASH_DELAY_MS` 测试钩子可模拟后台耗时。
- **多显示器 DPI 最大化修复（0px 间隙）**：物理像素工作区 + frame 补偿决策下沉 `WindowGeometry.ComputeMaximizedMinMaxInfo`；新增 `Win32/DisplayMetricsProvider.cs`、`Win32/MonitorDpiMetrics.cs`（`IDisplayMetricsProvider` 可注入，Headless 单测覆盖负坐标副屏/异构 DPI 边界）。
- **UI TestHook（NamedPipe，ADR-009）**：`DSH_TEST_MODE=1` 时激活的 `Win32/UiTestHook.cs` 内部通信通道，供自动化发 `ToggleMaximize`/`GetWindowRect`/`GetWorkArea`/`Shutdown` 精确验证窗口几何，生产路径零接触。
- **无头 UI 几何自测 `--ui-selftest`**：建窗→最大化→断言"窗口==工作区 0px"，退出码 0/1/2，结果落盘 `ui-selftest-result.txt`；配套 `.github/workflows/ui-test.yml` 在 windows-latest 跑几何回归。
- **跨屏最大化 E2E（无物理硬件）**：`scripts/install-virtual-display.ps1`/`Set-VirtualDisplay.ps1` 注入 IDD 虚拟副屏 + 异构 DPI（150%），`MaximizeAcrossVirtualDisplayTests` 断言最大化后窗口 ⊆ 副屏工作区（≤2px）。
- **架构决策索引**：`docs/DETAILS.md` 并入 ADR-001~008（WS_CAPTION / WM_NCCALCSIZE / WM_NCACTIVATE / F11 钩子 / 校验和源 / 日志轮转 / 原子写 / 生命周期）。

### 重构（架构，核心）

- **显式生命周期状态机（ADR-008）**：新增 `Lifecycle/LauncherLifecycle.cs` 纯内存状态机（Idle→…→Running / ShuttingDown / Failed，显式转移表，非法转移 Fail-fast）；新增 `WebViewCrashed` 触发器（Running 自转移：崩溃被拦截并触发重载，不终结应用）。
- **Manager 层（ADR-008）**：新增 `Managers/` 五个职责接口 + `RuntimeManager`（委托 RuntimeResolver，`confirmDownload` 注入保持"先确认后下载"契约）、`ServiceManager`（就绪探测，探针可注入）、`WebViewManager`（WebView2 事件接线迁入）、`WindowManager`、`TrayManager`、`F11LowLevelHook`。
- **组合根统一启动编排（ADR-010）**：`LauncherApp` 装配各 Manager 并把 `LauncherLifecycle` 的"状态→副作用"接线，**彻底替换 `Program.Main` 的旧 SplashForm 流水线**；解析 `DSH_WEB_URL`/`DSH_WEB_PORT`（修复硬编码 3080）；副作用（维护 IO/拉起服务/就绪探针/僵尸清理）经委托注入，自身不引用 Program。
- **`Program.cs` 瘦身 + 分步纯移动拆分**：窗体类迁出至 `Windows/`（`DshShellForm`/`SplashForm`/`TrayMenuForm`）、`CustomTitleBar` 迁出至 `Chrome/`、WebView2 事件接线迁入 `WebViewManager`（InitWebViewAsync/崩溃自愈/下载/弹窗策略）、托盘生命周期与主题监听迁入 `WindowManager`（依赖委托注入 + `VerifyDependencies` 接线自检）。
- **解除隐式循环依赖（ADR-011）**：`WindowManager` 对 `Program` 的 5 处静态引用（`CreatePopup`/`ApplyShadow`/`ShowWindowNative`/`ResolveDarkMode`/`Trace`）全部改为组合根注入的委托（`PopupFactory`/`ApplyShadowAction`/`ShowWindowAction`/`ResolveDarkModeProvider`/`TraceAction`），切断 Program↔WindowManager 环。
- **异常边界治理**：`WebViewManager` 全部静默 `catch{}`（8 处）改为捕获特定异常 + `Logger.Warn` 留痕；`RuntimeResult.Failed()` 保留错误码（E1002-E1005 诊断语义完整）。

### 修复

- **多显示器最大化 `ptMaxTrackSize` 修正**：Normal 态拖拽贴边时窗口不再比工作区小一圈（业界惯例：maxTrack 直接用物理工作区尺寸，maxSize 仍扣 frame 补偿 DWM 外扩）。
- **启动白屏 / 组件延迟绘制（回归根因）**：`LauncherApp.RunStartupAsync` 首个 await 前的同步阻塞（`TcpClient.Connect` 本机可达 2s、数据迁移 IO）曾卡死 UI 线程导致 Splash 先全白、控件后绘制——全部同步副作用已包 `Task.Run`，首个 await 即让出 UI 线程。
- **"正在检查 dsh 服务…"阶段卡顿（ADR-013）**：`ShellLogic.PortOpenAsync`（`ConnectAsync` + 3s 超时）、`ServiceManager` 探测异步化、HTTP 探测移入后台线程，检查期间窗体可拖动/重绘、取消按钮立即响应。
- **Splash 启动窗 UI 紧凑化**：窗体 440×232 → 380×196 → 380×180，边距统一 16px，消除"字少窗空"视觉失衡。
- **`RuntimeResult` 错误码保留**：修复 `Failed()` 工厂丢弃错误码问题，E1002/E1003/E1004/E1005 诊断语义完整。
- **CI 自测结果取回修复**：GUI 子系统应用经 `& exe` 时 `$LASTEXITCODE`/stdout 在 pwsh7 下不可靠回传 → 改为 `Start-Process -Wait -PassThru` + 结果落盘。
- **更新链路修复（rc6→rc7 检测不到 + 点击下载 E4001 + 应用卡死/取消无效）**：
  - `CompareVersions` 改完整 SemVer 比较，支持 `0.1.0-rc.x` prerelease（旧 `Version.TryParse` 对 `-rc` 解析失败恒判"无更新"）；
  - `ResolveLocalDshVersion` / `RunNpmCommand` 改经 `cmd.exe /c` 执行（CreateProcess 不解析 npm/dsh 的 `.cmd`/`.ps1` shim → 版本检测取不到、`npm pack` 下载恒 E4001）；
  - **更新应用可取消**：`RunNpmCommand` 增 `CancellationToken`，`ct.Register` 立即 `Kill` npm 进程树；`ApplyPendingDshUpdate`/`RunBackgroundMaintenance` 链式传 ct，取消保留 pending 下次启动再应用、不误计 E4002——修复"重启卡在启动服务、点取消几十秒才关"（阶段 0 npm install 30-60s 不可取消所致）；
  - `ApplyPendingDshUpdate` 上移到阶段 0 后台维护（不再伪装成"正在启动 dsh 服务…"），阶段 0 新增"正在准备启动环境…"文案；
  - 更新确认 `MessageBox` 带 owner + 调用前 `Activate()`，避免弹窗被遮挡不置前。
- **最大化 0px 间隙（e2e-geo 8px 缝隙回归）**：去除去 WS_CAPTION 窗口的无效 DWM frame 补偿——该类窗口最大化不外扩，补偿反而把窗口缩一圈（`DshShellForm` 改回无补偿重载 + `WindowGeometry` 文档修正）。
- **僵尸端口三重验证（"卡在等待服务就绪"根因修复）**：仅凭 TCP `PortOpen` 决定"跳过拉起"会误判僵尸服务（端口开但 HTTP 死）为健康，导致对半死服务傻等 180s 超时 E2002。新增 `ShellLogic.GetProcessIdByPort`（P/Invoke `GetExtendedTcpTable` + netstat 回退）+ `IsLikelyDshService` 进程身份校验 + 快速 HTTP 探测三重验证：`Healthy`（跳过拉起）/`Zombie`（`taskkill /T /F` 强杀 node + cmd/npx 祖先外壳后重启）/`Foreign`（非 dsh 进程占用，快速失败 E2004 提示端口冲突）。
- **Logger 防锁死 + fallback（诊断盲区修复）**：`File.AppendAllText`（内部 `FileShare.Read`）被残留 `cmd >> dsh.log` 重定向句柄阻塞时静默失败，导致启动诊断日志全丢。改为显式 `FileStream` + `FileShare.ReadWrite`；仍被独占锁死时写入 `%TEMP%\dsh-launcher-fallback-{pid}.log` 并向 `Console.Error` 输出 `[FATAL LOGGER]` 告警，Splash 黄色提示"日志文件被占用"；`WaitServiceReady` 错误标志检查回退读 fallback。
- **更新安装 UI 联动**：`ApplyPendingDshUpdate` 执行 npm install 期间 Splash 实时显示"正在应用更新 (vX)…"+ npm 逐行安装日志（`BeginOutputReadLine` 滚动转发，"added 50 packages"），并禁用取消按钮（防 npm install 中途强杀损坏 node_modules）；更新失败（非网络类）弹模态"自动应用更新失败，将继续使用旧版本启动，原因：…"，网络/超时类保留 pending 下次重试、权限/包损坏类清 pending 防死循环。
- **更新文案预期管理**：下载完成弹窗/托盘气泡/询问弹窗统一改为"下次重启启动器时将自动安装（预计需要 1-2 分钟，期间请耐心等待）"，消除"重启即生效"的误导。
- **更新链路改进（后台静默下载 + 本地直装）**：① 下载成功**不再弹 Modal**（不打断用户当前使用 harness），仅托盘气泡轻提示；② 应用更新**优先用下载时落地的本地 tarball**（`npm install -g <tarball>`，不 npx 现场拉主包），`pending-update.json` 记录 tarball 文件名（`StagedUpdate.LocateTarball` 三级定位：pending 名→命名规则→staging 模糊匹配）；③ tarball 缺失（缓存被清/旧记录）才回退线上拉取，Splash 如实显示"需要在线下载 dsh 组件，预计 1-2 分钟"。
- **修复主窗崩溃（0xc0000005，ImmSetOpenStatus）**：更新应用完成、主窗初始化 WebView2 时，WinForms 对宿主控件的 IME 状态管理在输入法活跃时偶发无效 HIMC 句柄 → 访问违规崩溃、窗口消失（用户反馈"重启后窗口消失"真凶）。修复：Form 与 WebView2 控件均置 `ImeMode.Disable`（页面输入法由 WebView2/Chromium 内部处理，WinForms 无需介入）。
- **更新文案诚实化**：`npm pack` 只下载主包 tarball（约 30KB，秒级正常），dsh 有 50+ 个 `@deepseek-ai/*` 依赖子包，重启安装时 npm 仍需在线解析——气泡/弹窗统一改为"主程序已下载，重启后自动安装（需联网解析依赖，预计 1-2 分钟）"，不再误导"已全部下载完、无需再次下载"。
- **后台依赖预热（重启秒装）**：后台 `npm pack` 后，在 `staging\prefetch_temp` 中执行一次完整 `npm install --prefix deps --no-audit --no-fund`，把全部 `@deepseek-ai/*` 依赖子包拉入全局 npm cache——重启时 `npm install -g <tarball>` 完全命中本地缓存，从"分钟级"降到"秒级"；预热与安装共用同一 `DSH_NPM_MIRROR` registry（防 cache miss）；预热失败仅 Warn 降级（不中断，保留 tarball 回退在线安装）；预热超时 180s 强制 kill；应用成功后清理 prefetch_temp 释放磁盘。
- **npm 执行机制加固（cmd shim + 路径解析 + 错误暴露）**：① `RunNpmCommand` 经 `cmd.exe /c` 执行（`CreateProcess` 不解析 `.cmd` shim 的基线，历史 E4001 根因）；② 新增 `ResolveNpmCmdPath`——优先从 `RuntimeResolver` 解析的 Node 根目录拼 `npm.cmd` 绝对路径（GUI 进程 PATH 缺 Node 目录时的隔离方案），失败回退 `where npm.cmd`，再失败回退 `cmd /c npm`；③ 下载失败弹窗暴露真实 `errorTail`（不再硬编码"下载失败"把原因藏进日志），并区分"未检测到 npm 环境（请安装 Node.js 18+）"与"网络/registry 问题（保留重试建议）"（`ShellLogic.IsNpmNotFoundError` 纯函数）；④ 预热/下载/安装三路径共享同一 `RunNpmCommand`，统一受益。
- **修复更新下载 E4001"文件名、目录名或卷标语法不正确"**：`DownloadDshUpdateStaged` 此前只 `CreateDirectory(staging)` 从未创建 `prefetch_temp`，`npm pack --pack-destination` 指向不存在的目录 → Windows 中文系统底层 fs 返回 ERROR_INVALID_NAME。修复：pack 前创建 `prefetchDir`。
- **修复 E4001 真正根因（cmd /c 引号剥离）**：`ResolveNpmCmdPath` 曾返回带引号的 npm 路径，`cmd /c "D:\node\npm.cmd" pack ...` 时 cmd 剥离首尾引号后引号计数错乱 → ERROR_INVALID_NAME（"文件名、目录名或卷标语法不正确"）。实测锁定正确形式：整行双层引号包裹 `/c ""D:\node\npm.cmd" pack ..."`（含空格路径亦安全）。修复：`ResolveNpmCmdPath` 改返回裸路径，`RunNpmCommand` 按 cmd 标准形式包裹；同时移除 `StandardErrorEncoding=UTF8`（显式 UTF-8 解码中文系统 GBK 输出反致 U+FFFD 乱码，.NET 默认 ANSI 即可正确解码）。
- **npm 执行引擎彻底重写（node.exe 直接执行 npm-cli.js）**：抛弃 `npm.cmd`/`cmd.exe /c`/`chcp 65001` 全部 Hack——`RuntimeResolver` 解析 node.exe 绝对路径 → `FindNpmCliJs` 两优先级探测 `npm-cli.js` → `node.exe "npm-cli.js" args`（UseShellExecute=false + 双编码 UTF-8）。实测验证：node 直接执行 `--version`/`pack` 均 EXIT=0，彻底根除 .cmd 编码冲突与 cmd /c 引号陷阱。保留 ct/progress/timeoutMs/workingDirectory 增强参数。
- **真实环境冒烟测试（打破测试幻觉）**：新增 `RealWorldNpmExecutionTests`——**零 Mock** 直接调 `RunNpmCommand("--version")` 验证真实 Node/npm 链路；`test.ps1` 设 `DSH_FORCE_NPM_SMOKE=1` 本地强制运行（无 Node 即失败阻断），CI 无 Node 时自动跳过。
- **SDET 测试体系重构（四大支柱 + Bug 驱动复现铁律）**：① 提取底层进程执行器 `RunProcessCaptured`（UTF-8 双编码捕获 + 超时 kill 僵尸树），`RunNpmCommand` 复用并供零 Mock 测试调用；② 新增 `RealOsProcessTests`（Category=RealOS）：`Regression_NpmCmd_Execution_And_Encoding` 真实 .cmd 输出中文断言无乱码不秒退、`RealOs_ZombieTree_Killed_On_Timeout` 真实进程树超时杀净、GBK 字节无乱码等；③ `LauncherAppScenarioTests` 补阶段 0 真实文件副作用断言（pending-update.json 真实落盘）；④ 新增 `realos-test.yml`（CI Stage 2：真实安装 Node.js 绝不 Skip，跑 Real-OS 测试）；⑤ 新增 `docs/TESTING-GUARDRAILS.md` 测试铁律 + AGENTS.md 强制引用（P0/P1 环境 Bug 必须写零 Mock 复现测试才合并）。
- **修复更新应用"依赖已预热5-10秒"虚假承诺（诚实承诺铁律）**：用户实测 cache 未预热时 `npm install -g` 现场下载 530 包需 450s（>120s 超时 E4002），文案却硬编码"预计 5-10 秒"误导。修复：① `pending-update.json` 新增 `prefetched` 标志——预热**真实成功**才为 true；② `ApplyPendingDshUpdate` 文案基于真实状态：prefetched=true → "依赖已就绪"（不写死秒数）、false/线上 → "可能需要几分钟"（如实管理预期）；③ 下载气泡同步诚实化；④ 契约测试锁定 prefetched 语义（不传/旧记录 → false，绝不得谎报）。

### 测试

- Headless 状态机/组合根场景测试（`LauncherAppScenarioTests` + `LauncherLifecycleTests`）：Happy Path（状态轨迹 + UIInitialized 事件）、Runtime Failure（E1004）、Readiness Timeout（E2002 + 僵尸清理回调）、WebView2 崩溃恢复（自转移 + 广播）、异常边界（Manager 抛异常不悬停状态机）、非法转移 Fail-fast。
- TestHook E2E（`UiTestHookE2ETests`）：`ToggleMaximize` + `GetWindowRect`/`GetWorkArea` 断言最大化 0px 间隙（≤2px）、`Shutdown` 优雅退出。
- 启动耗时基准（`StartupLatencyTests`，Splash 窗口 <500ms）与 UI 响应性/渲染完整性（`UiResponsivenessTests`，后台 10s 阻塞期 UI 健康 + 无空白）。
- 跨屏最大化 E2E（虚拟副屏，见上）。
- 纯逻辑测试补齐：`SuggestDownloadName`（RFC5987）、`ShellLogic.AtomicWrite`、`F11HookDecisionTests`、`WindowStateStore` 最大化状态。
- **多显示器 Headless 化（v0.4.0，替代 CI 内核虚拟显示驱动）**：`IScreenProvider` + `FakeScreenProvider`（注入任意数量/分辨率/DPI 假屏拓扑）+ `MultiMonitorContractTests`（副屏正常/拔掉越界容灾/高 DPI 逻辑物理混用）+ `ScreenProviderIntegrationTests`（4K+1080p 拓扑接线）；`Set-VirtualDisplay.ps1` 修 CS8632（`#nullable enable`）保留本地调试；`MaximizeAcrossVirtualDisplayTests` 加无副屏守卫 + 还原路径用例（issue#17 副屏最大化/还原丢窗）。
- **E2E 稳定性加固**：禁用并行（全局单实例 Mutex 竞争导致窗口不出现）；UIA 控件查找轮询等待（窗口就绪≠控件树就绪，CI 偶发 null）；取消触发改 UIA InvokePattern（鼠标 Click 不激活前台窗口导致取消不生效）；进程退出断言改轮询。
- **僵尸端口/日志锁/更新进度契约测试**：`ServiceManagerTests` 三重验证四态（Closed/Healthy/Zombie/Foreign）+ `ZombieCleanup_PortOccupiedButHttpFails_KillsProcessTree`（杀 node + 祖先 cmd/npx 外壳 + 端口释放）；`LauncherAppScenarioTests` 僵尸清理成功重启/失败 E2004 快速失败/非 dsh 占用不误杀；`LoggerTests.Logger_Lock_Fallback_MainLockedByFileShareNone`（`FileShare.None` 独占 → fallback 含完整日志）+ 路径阻塞 fallback；`UpdateFlowContractTests` 更新进度上报（"正在应用更新"+ npm 日志）、更新失败不阻断启动（旧版继续）、`IsRetryableNpmError` pending 保留/清理契约（Theory 11 例）。
- 单测 **407 个全部通过**（含真实环境冒烟 + 真实 OS 交互测试）。

## [Unreleased]

## [0.3.5] - 2026-08-18

> 代码质量审查修复批次（P0-2/P0-3 + P1）：供应链/日志契约/进程管理加固。

### 修复

- **便携 Node 校验和源解耦（P0-2，供应链）**：`SHASUMS256.txt` 优先从官方 `nodejs.org` 拉取，与 zip 下载镜像源解耦——避免镜像被投毒时 zip 与校验和一起被替换，`SHA256` 防篡改真正生效（官方失败回退镜像）。
- **日志轮转活服务守卫（P0-3，日志契约）**：崩溃残留的孤儿服务若仍用 `cmd >>` 持有 `dsh.log`，提前轮转会把日志劈裂成两段；现仅当无活服务占端口时才轮转。
- **ResolveTarget 契约覆盖（P1-5）**：`DSH_WEB_PORT` 支持合并进 `ShellLogic.ResolveTarget`，生产委托调用，契约测试覆盖生产路径。
- **注册表根键释放（P1-8）**：`ReadCandidateProducts` 四个 `OpenSubKey` 根键 Dispose，防"清理旧版本"路径句柄泄漏。
- **子进程超时处置（P1-9）**：`IsUsableNode`/`RunCapture` 超时即 `Kill`（防 `node --version` 挂死泄漏），并异步排空管道防阻塞。
- **非客户区重绘节流（P1-13）**：`ForceNonClientRedraw` 仅在窗口状态（最大化/还原/最小化）变化时调用，拖动缩放不再高频重算框架。
- **JSON 原子写（P1-10）**：新增 `ShellLogic.AtomicWrite`（临时文件 + `File.Move` 覆盖），`WindowStateStore`/`StagedUpdate`/`RecordLastMirror` 共用，防退出瞬间崩溃留下半截 JSON。

### 测试

- `ResolveTarget` 新增 `DSH_WEB_PORT` 5 例（含非法/越界/URL 优先级）。
- `Sanitize` 新增"独立波浪号不替换"用例，锁定脱敏行为。

## [0.3.4] - 2026-08-18

> 修复：F11 全屏可靠化、最大化精确铺满（消除 4px 间隙）、焦点切换无经典标题栏闪影、dsh 下载镜像加速。

### 修复

- **F11 全屏可靠化（物理按键）**：物理 F11 的 `WM_KEYDOWN` 有时被 WebView2 浏览器进程截走，不进入 WinForms 消息队列（`KeyDown`/`ProcessCmdKey`/消息过滤器均不可靠）。改用**系统级低级键盘钩子（`WH_KEYBOARD_LL`）**在 OS 层捕获 F11，仅在主窗口前台时切换最大化/还原并吞掉该键，与焦点/浏览器进程/重启无关。
- **最大化精确铺满（消除 4px 间隙）**：去掉 `WS_CAPTION`（含 `WS_BORDER|WS_DLGFRAME`），仅保留 `WS_THICKFRAME|WS_MINIMIZEBOX|WS_MAXIMIZEBOX|WS_SYSMENU`；`WM_GETMINMAXINFO` 直接设为工作区尺寸/位置。DWM 不再为原生标题栏预留空间、不再把窗口向外扩展，最大化窗口 == 客户区 == 工作区，四周 **0px 间隙**、无负坐标、不覆盖任务栏（#17 保持修复）。
- **焦点切换无经典标题栏闪影**：拦截 `WM_NCACTIVATE`/`WM_NCPAINT` 并吞掉（返回 1/0），避免 DefWindowProc 用经典 NC 渲染器画出"老式 win98 标题栏"；`WM_NCACTIVATE` 末尾追加 `SWP_FRAMECHANGED` 兜底重绘。
- **标题栏永不移除 / 按钮不再消失**：F11 语义改为"最大化/还原"，标题栏始终保留；`OnResize` 自愈强制标题栏可见 + `LayoutChrome` 统一布局 + `Invalidate` 清除 Aero Snap 残留（此前最大化还原后按钮消失的根因——v0.3.3 为未复现 issue#15 添加的 `ContainsFullScreenElementChanged` 处理器已被移除，页面 HTML 全屏回归 WebView2 默认行为）。
- **最大化状态持久化**：`WindowStateStore.WindowState` 新增 `IsMaximized`，最大化后关闭再启动恢复最大化。
- **dsh 下载镜像加速**：`start-dsh.vbs` 的 `npx` 路径默认走 `npmmirror`（国内可直连），可用 `DSH_NPM_MIRROR` 覆盖——dsh 本体下载不再卡在慢 npmjs。
- **VBScript 缺少对象弹窗（800A01A8）**：`start-dsh.vbs` 显式 `Set f = Nothing` 初始化 + `OpenTextFile` 前 `CreateFolder`，并清理旧版指向 `start-dsh.vbs` 的 autostart 残留。

### 测试

- 新增 `F11HookDecisionTests`（低级键盘钩子判定纯函数：F11 且前台才处理、非 F11/非前台放行）。
- 新增 `WindowStateStore` 最大化状态（`IsMaximized`）往返与旧版 JSON 向后兼容测试。

## [0.3.3] - 2026-08-17

> 修复：全屏窗口消失（#15）、最大化记忆丢失、VBScript 缺少对象弹窗。

### 修复

- **全屏窗口消失（#15 候选根因）**：`InitWebViewAsync` 新增 `ContainsFullScreenElementChanged` 事件处理——全屏时隐藏自绘标题栏、WebView2 填满客户区，退出全屏时恢复。此前无此处理，WebView2 内部全屏状态变化后页面可能渲染异常。
- **最大化状态未持久化**：`WindowStateStore.WindowState` 新增 `IsMaximized` 字段，`SaveWindowState` 保存最大化标志，启动时恢复。
- **最大化时窗口超出工作区**：`WM_NCCALCSIZE` 中最大化分支将客户区钳制到工作区范围，消除 `WS_CAPTION|WS_THICKFRAME` 不可见边框（8px）导致的窗口超出可视区域问题（Windows 25H2 上可能更明显）。
- **VBScript 缺少对象弹窗（800A01A8）**：`start-dsh.vbs` 显式 `Set f = Nothing` 初始化变量，避免 `OpenTextFile` 失败后 `f` 为 `Empty` 导致 `Is Nothing` 引发"缺少对象"错误——此前 `On Error Resume Next` 静默吞掉该错误使回退分支不执行，最终弹窗报错。

## [0.3.2] - 2026-08-16

> 普通更新：修复任务栏"再点一次最小化"失效，并完成稳定性 / 诊断 / 契约 / 测试收敛质量治理批次（无新增用户可见功能，非安全更新）。

### 修复

- **任务栏"再点一次最小化"失效（用户反馈）**：主窗口 `CreateParams` 补回 `WS_MINIMIZEBOX|WS_MAXIMIZEBOX`（此前只补了 `WS_CAPTION|WS_THICKFRAME`，而 Explorer 只对带最小化框的窗口做任务栏收起切换）——任务栏点击现在可正常"打开/收起"切换，Alt+Space 系统菜单同步恢复最小化/最大化项。
- **启动中途取消不再产生无主服务（P0-1）**：取消启动时若服务已在后台下载/启动，已监听则记录服务 PID 供下次启动接管（此前无 pid 文件 → `TryAdoptOrphanService` 无法认领，服务永久无主占端口）；"取消"从内部错误 E9001 改为独立码 **E2006**。
- **崩溃留痕（P0-2）**：挂未处理异常钩子（UI 线程 `Application.ThreadException` + 全局 `AppDomain.UnhandledException`），任何崩溃先写 E9001 日志（含异常全文）再退出——此前崩溃零留痕，无法诊断。
- **进程杀灭加固（P1-3）**：taskkill 加 `/T`（子进程树一并清理）；杀前校验 PID 确在监听目标端口（防 PID 复用误杀无关 node；`SweepStaleServicePid` 对"活着但不监听"的记录改为只清 pid 文件不误杀）。
- **状态文件损坏告警（P1-4）**：window-state.json / pending-update.json 损坏不再静默回退，补 Warn 日志（对齐 settings.json 治理）。
- **主题轮询降频（P1-2）**：settings.yaml 按 mtime 缓存重读 + 轮询 500ms→2s——主窗打开期间不再持续全量读磁盘文件（watcher 仍是主通道）。

### 测试

- **契约防线（P1-6）**：抽出 HTTP 就绪 / TCP 端口探测 / Node 版本门槛三个契约纯函数并新增契约测试（`ContractTests`，FakeHttpMessageHandler / 环回 socket，不碰网络）——防上游 dsh 行为变更无声破坏；`IsLikelyDshService` 负向分支补单测。
- **测试资产清洗（P1-1）**：删除 2 个依赖真实环境的"永真假绿灯"测试与 2 个恒真/子集测试；合并 `CompareVersions`/`ResolveTarget`/`IsSafeToOpen`/`ShouldRotate` 跨文件重复；`ReadLogTail`/`TailLines` 双实现合一（共享读）；新增 `LoggerState` 串行集合（消除静态 Logger 状态跨类并行串扰隐患）。
- **负向套件 +1（N9）**：`DSH_TEST_CRASH=1` 触发未捕获异常 → 断言 E9001 崩溃留痕生效。

### 变更

- **CI 去冗余（P1-5）**：`test.ps1` 成为唯一测试入口（此前 build.yml 独立步骤 + test.ps1 内部双跑 dotnet test）；`git rm --cached` 移除误追踪且 0 引用的 WiX Util 扩展 DLL。

## [0.3.1] - 2026-08-16

> **重要更新（SECURITY 标记）**：本轮包含多项安全与稳健性修复——诊断包脱敏与共享读、WebView2 数据目录互锁防护、更新降噪与下载缓存管理、便携环境自检、窗口记忆与镜像回退修复；建议所有旧版本用户更新。
> v0.3.0 规划中 P2 储备的六项全部落地（commit 27881f8，单测 140/140）：WebView2 缺失自动修复、MSI 安装时 winget 自动装 .NET、SIGINT 优雅终止、Node 默认 LTS 升级、日志超长告警、镜像路由纯函数化。

### 新增

- **WebView2 缺失兜底（自动修复）**：WebView2 初始化失败时，先静默安装 Evergreen Bootstrapper（官方固定链接下载约 2MB → `/silent /install` → 重试初始化），仍失败才弹 E1006；不再一上来就让用户手动装 WebView2。
- **MSI 前置检查 winget 自动装 .NET**：`PrereqCheck.exe` 缺 .NET 时弹「自动安装(A)」，一键 `winget install Microsoft.DotNet.DesktopRuntime.10 --silent --accept-package-agreements --accept-source-agreements`（10 分钟超时），装完重测满足即继续安装；winget 缺失回退下载页；仅缺 Node 时不显示自动安装按钮。
- **常驻超长日志告警**：启动早段检测到 `dsh.log` >50MB 且最后写入 >24h → 记 `Warn`（轮转留给下次重启——热轮转会被运行中 node 的句柄阻止，故只告警不折腾）。
- **启动状态窗"取消"真正生效（质量治理）**：此前点"取消"只关窗，后台任务继续跑、UI 最长假死 180s；现在取消即真正终止等待流程（commit a052b50）。
- **进程终止前身份校验（质量治理）**：PID 复用防护——只杀 node 服务进程，壳与卸载 CA 两处都校验；强杀后确认，杀不干净则保留 pid 文件供下次启动认领。
- **渲染进程连续崩溃上限（质量治理）**：10s 窗口内连续崩溃 3 次 → 停止自动重载并记 E1007，保留托盘唤窗手动恢复（不再死循环重载）。
- **插件弹窗崩溃不再污染主窗口恢复标志（质量治理）**：弹窗崩溃不再误触主窗口的崩溃恢复标记。
- **"已下载待应用"更新启动时气泡提示一次（质量治理）**：服务健康跳过应用、或应用失败时都不再静默，附手动 `npm` 命令供用户自行处理。
- **错误码 E1007 + 测试与 CI 纳入（质量治理）**：新增 E1007（渲染进程反复崩溃）；CI 纳入 `scripts/test.ps1`（无 `-Smoke`）作为 PR gate；新增错误码契约单元测试（R02）。
- **测试体系扩大（质量治理，单测 147→255）**：新增 `UpdateCheckerTests`（版本拉取 JSON 解析/安全更新判定/比较边界，FakeHttpMessageHandler 注入不碰网络）、`DiagnoseExportTests`（脱敏/级别过滤/错误汇总/参数解析）、`LoggerTests`（级别阈值/JSON 结构/写失败静默）、`SecurityBoundaryTests`（可执行面绝不自动打开 22 条/权限白名单全枚举/下载命名边界）；`DiagnoseExport` 纯函数改 internal 供单测。
- **E2E 全旅程测试（scripts/e2e-test.ps1，37 断言）**：发布产物完整性 → 免安装 zip 解压部署 → 真实 GUI 首启（探针带进程身份+类名校验，绝不误操作真实窗口）→ 窗口记忆端到端 → 服务锁定下诊断导出 → 卸载清理 → `-CleanData` 数据边界。负向套件新增 N8（日志锁定共享读）。

### 变更

- **Node 便携默认 LTS 升级**：`v22.16.0` → `v24.15.0`（2026-08 核对：Node 24.x Active LTS，支持至 2028-04，最大化支持窗口）；`DSH_NODE_VERSION` 仍可覆盖。
- **SIGINT 尽力而为优雅终止**：停服务先 `TryGracefulStop`（`AttachConsole` + `CTRL_BREAK`，node 映射 SIGBREAK 可选清理），无控制台进程时自动降级温和 `taskkill`，仍不退才 `/f`（等待窗 1.5s）——替代此前直白 taskkill。
- **崩溃自愈策略调整（质量治理，与其在"新增"的崩溃上限条目合并）**：从"反复自动重载"改为"连续崩溃达阈值即停止、保留手动恢复"；GitHub/npm 更新检测与服务/就绪判定等超时整体放宽（如更新检测 8s→15s，弱网不再误报"无更新"）。

### 修复

- **便携 Node 镜像路由去重**：`BaseUrls` 重构为纯函数（`DSH_NODE_MIRROR` → 上次成功源 → nodejs.org → npmmirror，`Distinct` 去重），消除返回链重复的可能，新增 4 个单元测试。
- **错误日志级别失真（质量治理）**：用户取消/拒绝被记为 Error 污染错误汇总、E4001 双写 → `ShowError` 支持级别参数与去重。
- **--diagnose 服务运行时失败（质量治理，发版前实测发现）**：dsh 服务经 `cmd >>` 重定向独占写 dsh.log，`File.ReadLines` 默认共享模式被拒 → 22 字节空 zip + E5001 写不进被锁日志；改为 `FileShare.ReadWrite` 共享读（TailLines/FilterByLevel/SummarizeErrors 统一）。
- **WebView2 数据目录测试隔离（质量治理，实测事故）**：测试实例与真实实例共用 `%LOCALAPPDATA%\DshWeb\WebView2` user-data-dir 会互锁导致真实启动器整窗灰死；新增 `DSH_WEBVIEW2_DATA` 测试钩子，负向/E2E 全部用例强制隔离。
- **E1006 兜底失败无诊断日志（质量治理）**：WebView2 静默安装兜底改为区分下载/安装/超时三阶段记录。
- **删除死码（质量治理）**：移除废弃错误码 E1001/E3001 与 `ShellLogic.ResolveLogPath`；`ReadLogTail` 改流式读取（大日志不整读）。
- **WebView2 共享环境互斥（质量治理）**：共享环境创建加锁，消除并发弹窗的创建竞态。
- **主题监听资源释放（质量治理）**：真实退出时释放 SystemEvents/FSW/轮询 Timer，消除关窗后毫秒级竞态。
- **托盘创建失败不静默（质量治理）**：创建失败记 `Warn` 日志。
- **配置降级精确判定（质量治理）**：只在**顶层** `serviceLifetime` 键命中时触发，消除子串误报；插件在但值越界也清理。
- **更新检测超时放宽（质量治理）**：GitHub/npm 更新检测超时 8s→15s，弱网不再误报"无更新"。
- **--diagnose 脱敏增强（质量治理）**：额外替换 `%USERPROFILE%`、`~\`、`\用户名\` 常见路径片段，减少路径泄漏。
- **--diagnose zip 路径落日志（质量治理）**：成功导出后 `Logger.Info` 记录产物路径——GUI 用户无控制台时也能在 dsh.log 找到诊断包位置。
- **下载缓存管理（质量治理）**：`DataDir\staging` 中超过 7 天的过期下载包启动时自动清理；更新应用成功后整体清空——消除下载缓存无限增长。
- **便携版缺 .NET 环境自检（质量治理）**：新增 `check-prereq.cmd`（纯 cmd、零依赖，随 MSI/zip 发布）检测 .NET Desktop Runtime 10 / WebView2 / Node 18+，缺失时给出中文指引与安装链接——解决"便携版双击无反应"的排障入口。
- **WebView2 数据目录互锁专属提示（质量治理）**：初始化失败含 `0x800700B7`（user-data-dir 被另一实例占用）时，提示"另一个 dsh-launcher 正在运行"而非泛化 E1006——真实多开不再误以为 Runtime 缺失。
- **环境检测回退日志（质量治理）**：settings.json 非法 JSON/非对象、Node 解析失败（PATH/注册表/便携各自区分"版本过低或损坏"）、窗口位置保存失败 → 均记 Warn——此前静默回退无法诊断。
- **便携 Node 确认框区分原因（质量治理）**：文案区分"未检测到 Node.js"与"系统 Node.js 版本过低或不可用（需要 18+）"，用户不再困惑"为何有 Node 还要下载便携版"。
- **更新失败气泡降噪（质量治理）**：pending 应用失败累计 failCount，达到阈值后启动不再弹"待应用"气泡（降级为仅日志，手动 npm 命令保留在日志）；`MarkApplyFailed` 幂等。
- **用户拒绝更新持久化跳过（质量治理）**：拒绝 dsh 更新后写入 `skipped-update.json`，该版本不再每次启动提示；检测到更新的版本时重新提示。
- **托盘按需策略修正（质量治理）**：托盘只在**托盘驻留**模式下常驻显示（关窗藏到托盘需唤窗入口）；"常驻"模式关窗即退出壳（服务保留、下次启动自动开窗）、"跟随窗口"关窗全退，两者都不需要托盘——此前"装了插件就显示托盘"与"默认不打扰"承诺不符。
- **移除主题诊断链（质量治理）**：插件设置页"壳当前深浅色主题"文案与配套 `/dsh-launcher-lifetime/theme` 路由、壳侧 `theme.json` 写入一并移除（dsh-launcher-lifetime 0.2.1：src 删除 + 重新构建 + 同步已安装实体）。

## [0.3.0] - Unreleased

> **注**：v0.3.0 未单独发 tag，其全部内容随 [0.3.1] 一并发布（2026-08-16）。
>
> 底座重构版本：可观测性统一（一份日志、一套错误码、一键诊断）＋更省心的环境与生命周期（便携 Node、延迟更新、托盘按需）＋更强的稳健性（僵尸清理、窗口容灾）＋更清楚的数据边界。（重构规划文档已归档，架构决策见 [docs/ARCHITECTURE_DECISIONS.md](docs/ARCHITECTURE_DECISIONS.md)）

### 新增

- **一键诊断导出**：`DshWeb.exe --diagnose [--min-level warn|error]` 把统一日志（可按级别过滤）、环境变量、node/dotnet/webview2 版本、错误码汇总打成**脱敏** zip（用户目录替换为 `%USER%`）放到"下载"文件夹，便于无脑汇报——绝不含 `.credentials.yaml` / 会话 / 存储 / 插件内容。
- **Node.js 便携自动补齐**：检测不到 Node.js 时弹一次性确认框，自动下载 LTS 便携版到 `%LOCALAPPDATA%\dsh-launcher\env\node\`（SHA256 校验 + 镜像回退 nodejs.org → npmmirror，`DSH_NODE_VERSION` / `DSH_NODE_MIRROR` 可覆盖），只改进程级 PATH、不改系统环境变量与注册表。
- **窗口位置记忆与多显示器容灾**：窗口位置/大小持久化到 `window-state.json`；副屏拔掉等越界时回退主屏工作区居中并钳制，任务栏变化时整格钳制进工作区。

### 变更

- **统一日志**：旧 `shell.log` 与 `%USERPROFILE%\.dsh-web*.log` 的多文件方案收敛为**单一文件** `DSH_HOME\dsh-launcher\dsh.log`（默认 `~/.dsh\dsh-launcher\dsh.log`）——壳写 JSON Lines（级别 Info/Warn/Error，`DSH_LOG_LEVEL` 控制最小级别），dsh 服务输出经 `start-dsh.vbs` 追加同文件共存；轮转由壳独家负责（>30MB 或 >3 天 → `.1/.2`，保留 ≤3 份）。旧日志路径不再产生新文件。
- **错误码**：所有用户可见错误弹窗带 `[E####]` 码（目录见 `src/DshShell/ErrorCodes.cs`：E1001 未检测到 Node、E2002 服务启动超时、E2011 插件缺失配置降级等），与结构化日志的 `code` 字段、诊断导出的错误码汇总共用同一套码，消息可 Ctrl+C 复制。
- **托盘按需显示**：默认隐藏；仅当检测到 dsh-launcher-lifetime 插件已安装、或本会话有待通知的更新时才显示托盘。
- **dsh 非侵入式更新（延迟应用）**：点更新气泡 → 确认 → 后台 `npm pack` 下载到 `DataDir\staging`（不碰运行中的环境）→ 下次启动拉起服务前自动应用（`npm install -g` 固定版本，写入 `pending-update.json`）；失败不阻塞。
- **配置自动回退（插件降级）**：dsh-launcher-lifetime 插件卸载后，壳自动忽略并抹除 `settings.json` 里残留的 `serviceLifetime`（回退"跟随窗口"），无需手动删 JSON。
- **僵尸进程清理 + 孤儿健康校验**：启动时清理上次崩溃遗留的僵尸 Node 进程（只动 pid 文件记录的 PID、绝不按进程名批量杀）；孤儿服务健康（HTTP 就绪）才接管复用，坏状态则清理并重建。
- **卸载清理数据边界**：MSI 卸载自动清理 `DSH_HOME\dsh-launcher\`（自身配置/统一日志/窗口状态等）与旧 `%USERPROFILE%\.dsh-web*.log`；便携版 `uninstall-autostart.cmd -CleanData`（显式可选）同边界清理。**绝不触碰** `profiles/`、`settings.yaml`、`.credentials.yaml`、sessions、插件等 dsh 生态数据。

### 移除

- 移除了旧的 `shell.log` 与 `%USERPROFILE%\.dsh-web*.log` 日志文件方案（统一为 `dsh.log`）；停用 `start-dsh.vbs` 的自行截断/轮转（所有权归壳）。

### 说明

- 被拒/降级方案已归档至 [docs/ARCHITECTURE_DECISIONS.md](docs/ARCHITECTURE_DECISIONS.md) 附录 A：主题 accent 增强（dsh 无法读取自定义主题色）、镜像延迟测速、运行时静默装 .NET（技术不可能，改 MSI 链路 winget，P2 储备）、SIGINT 优雅终止（降级 P2）、自制下载管线（不建，npm 当下载器）。

## [0.2.5] - 2026-08-15

### 变更

- **自启改为"拉壳"方案**：HKCU Run 的 dsh-launcher 值从 `wscript ...start-dsh.vbs`（静默起服务）改为直接指向 **`DshWeb.exe`**——登录 → 壳窗口出现 → 壳自行探测/拉起 dsh 服务（未运行→跑 start-dsh.vbs + 状态窗，已运行→收养）。壳全程管理服务生命周期，自启不再依赖独立的 vbs 静默服务路径；旧版 wscript+vbs 格式的存量 Run 值会被壳首启自动迁移为新格式

### 修复

- **勾选"开机自启"后重启不自启（0.2.5 发版前实测发现）**：两级落地要求壳首次启动补写 HKCU Run，但用户自然流程是"装完勾选→直接重启"，壳从未运行，HKCU Run 永远不落地。修复：安装 CA 同时写 HKCU Run（UAC 提权下 msiexec 服务进程以发起用户身份运行，写真实用户 hive 可靠），壳首启自愈保留作兜底。
- **勾选"开机自启"后标志不落地（0.2.4 发版后实测发现）**：0.2.4 使用 MSI Feature Level 条件控制自启标志组件安装，但 MSI 在修改安装场景下对 Absent feature 的 Level 条件不重新评估——实测 AUTO_START_OPTION=1 已设置但 Feature Request 仍为 Null。尝试改用 Component 条件同样失效（条件已写入 MSI 但组件仍被无条件安装）。最终改为 immediate 自定义动作 `SetAutoStartFlag` 直接写 HKLM 注册表值，绕过组件/Feature 条件机制，所有场景（全新安装/修改安装/升级安装/修复）一致可靠。

## [0.2.4] - 2026-08-15

### 修复

- **安装向导"开机自启"默认显示为勾选但实际未启用（UI 与实际不一致）**：MSI CheckBox 控件对存在非空值的属性（哪怕 "0"）会渲染为勾选状态，而 feature 条件层面 "0" 又不装组件——用户看到勾上了、实际没自启，很可能也是上游 issue 报告者的遗误诱因。修复：默认不勾的 checkbox 属性必须无默认值（属性不存在 → 不勾；勾选 → "1"；取消 → 空串），与 WiX 官方 FAQ 推荐做法一致。
- **per-machine 安装勾选"开机自启"后 HKCU Run 值不落地、登录不自启（issue 实测报告）**：根因是 per-machine 提权安装中 `RegistryValue Root="HKCU"` 写入不可靠（值落到提升上下文或被静默丢弃，所有用户 hive 均扫不到）。改为两级落地：MSI 勾选时只写机器级意图标志（`HKLM\Software\dsh-launcher\AutoStartWanted=1`，可靠、随卸载自动清除），壳首次启动时读到标志后以当前用户身份补写 `HKCU\...\Run`——用户上下文写 HKCU 100% 可靠，交互/静默安装均覆盖；也顺带解决"其他管理员过 UAC 时自启写错 hive"的问题（谁先用壳，自启就落在谁头上）。升级/自定义目录导致路径变化时自动更新。
- **卸载时清理 HKCU Run 自启值**：per-machine 卸载同样无法用注册表组件可靠删 HKCU（对称问题），改由 immediate 自定义动作（发起用户上下文）删除，只删内容包含 `start-dsh.vbs` 的同名值，失败不阻断卸载。
- **卸载时 HKLM 意图标志残留（Level 条件在卸载时重评的隐蔽行为）**：MSI 对 Level=0（条件禁用）feature 的组件在卸载时不请求移除——实测组件 Installed: Local 但 Request: Null，注册表值残留。修复：卸载 CA 兜底同时删除 HKCU Run 与 HKLM 标志，调度条件加 `NOT UPGRADINGPRODUCTCODE`（升级链路不触发，用户已启用的自启跨版本保留）。
- **`uninstall-autostart.cmd` 同时清除 HKLM 意图标志**（需管理员）：防止壳自愈机制在下次启动时重新创建用户刚手动删掉的自启项。

## [0.2.3] - 2026-08-15

### 修复

- **窗口贴边（Aero Snap）失效（0.1.10 自绘标题栏引入的回归）**：拖到屏幕边缘半屏/拖顶最大化/Win+方向键全部恢复。根因是 `FormBorderStyle.None` 剥掉了 `WS_CAPTION|WS_THICKFRAME` 样式位；改为加回样式位 + `WM_NCCALCSIZE` 吃掉原生框架预留（Chromium / Windows Terminal 同款方案），自绘标题栏与 1px 边框观感不变，附带恢复 Win11 原生圆角/阴影/最小化动画与 Alt+Space 系统菜单。
- **托盘右键菜单点击其他位置不消失**：菜单窗从未被激活过则永远收不到失活消息（根因），弹出时显式 `Activate()` 抢占激活，点击任意其他窗口/桌面即关闭；Esc 关闭保持；关闭时顺手释放淡入 Timer 与菜单字体。

### 调整

- **托盘菜单"退出"字重再降一档**：Medium(500)/伪粗体双画 → Regular(400) 单画（Noto Sans SC → DengXian → Microsoft YaHei 回退链不变），与 1.8px 图标描边视觉平衡。

## [0.2.2] - 2026-08-15

### 修复

- **MSI 安装失败（0.2.1 撤回原因，严重）**：0.2.1 加入的 .NET Runtime 检测（RegistrySearch + LaunchCondition）因 **WiX 5.0.2 的 AppSearch/Signature 表缺陷**（AppSearch 表引用 `Signature` 表但该表条目缺失）导致检测属性恒为空 → 条件恒假 → **任何机器（即使已装 .NET 10）安装都报"需要 .NET Desktop Runtime 10"并中止（1603）**。0.2.2 移除该方案
- **安装前置检查改为独立检测程序**：新增 `PrereqCheck.exe`（Type-38 外部 exe，与文件夹选择器同模式）在向导启动时（InstallUISequence 最前）检测 **.NET Desktop Runtime 10**（shared 目录 10.x 存在性）与 **Node.js 18+**（PATH 可执行 + 注册表兜底）；任一缺失 → **弹窗列出缺失项并提供"去下载"按钮**（打开 .NET 官方下载页 / nodejs.org），缺失或取消即中止安装（`Return="check"`）；**弹窗 60 秒无响应自动按"否"中止**（兜底静默/无人值守场景不挂起）；升级/修复/卸载不拦截（`NOT Installed` 条件）
- **安装前置检测不影响正常安装**：环境满足时静默通过（实测安装/升级/卸载全部 exit=0）

### 变更

- **安装前置检查更完整**：除 .NET Runtime 外同时检测 Node.js（dsh 服务运行必需），引导下载对应正确版本

## [0.2.1] - 2026-08-15（已撤回）

> **注意**：0.2.1 因上述 MSI 安装失败问题已从 GitHub 撤回，请勿使用该版本；请用 0.2.2。

### 变更

- **MSI 安装向导前置检查 .NET Desktop Runtime 10**：缺失时安装前明确提示（附 `winget install Microsoft.DotNet.DesktopRuntime.10` 指引），不再出现"装完双击无反应"；检测 WOW6432Node 视图下 `sharedfx\Microsoft.WindowsDesktop.App` 的 10.* 版本值（SDK 自带 runtime 与独立安装器均会写该键）
- **托盘菜单字体回退链**：Noto Sans SC Medium（思源黑体 500，原生加粗）→ DengXian（等线）→ Microsoft YaHei UI → 系统默认；等线/雅黑无中间字重时伪粗体双画补粗，缺字体静默降级
- **MSI 目录选择器回写校验**：文件夹选择结果带一次性随机令牌（Guid），安装动作校验令牌匹配且路径为本地绝对路径、拒绝系统目录（Windows/Program Files/ProgramData 等）后才采纳，防低权限攻击者预置伪造路径
- **下载完成智能打开**：仅无害扩展名（图片/文本/pdf/音视频/压缩包等）自动用默认程序打开；其余（.html/.svg/.hta/.exe 等可执行代码面）落盘后托盘气泡提示，不自动执行
- **主窗口导航白名单**：只允许本地（127.0.0.1/localhost）导航，外部 http(s) 导航自动转系统默认浏览器——壳无地址栏，防被重定向到伪站点
- **CI 供应链加固**：第三方 action 全部 pin commit SHA；顶层 `permissions: contents: read`（仅 Release 步骤放开 write）；tag 名经环境变量注入不再内插进脚本
- **start-dsh.vbs 日志重定向加引号**：用户目录含空格/元字符时不再截断日志路径或注入命令（实测复现修复）

### 修复

- **副屏负坐标窗口边缘缩放失效**（B1）：WM_NCHITTEST 的 64 位 lParam 用 `ToInt32()` 在左侧/上方副屏抛 OverflowException，改为有符号 16 位拆位
- **单实例误聚焦插件弹窗**（B2）：弹窗初始标题不再与主窗口同名（"dsh-launcher 弹窗"），第二实例按标题找主窗口时不会误聚焦 popup
- **托盘菜单淡入 Timer 泄漏**（B3）：动画完成后 Dispose（每次弹菜单一个，不再等 GC）
- **托盘菜单位置屏幕外**：屏幕边界自适应——左/上越界翻转到鼠标另一侧，仍越界贴工作区边缘（左侧竖排任务栏时菜单不再被推出屏幕）
- **托盘菜单透明不显示**：`CreateCompatibleDC`/`SelectObject`/`DeleteDC`/`DeleteObject` 四个 P/Invoke 的 DLL 归属修正为 gdi32.dll（此前误标 user32.dll 导致渲染异常被吞、菜单全透明）
- **卸载后 ProgramData 空目录残留**：壳启动时清理中转文件与空目录（非空则不动，不删第三方文件）
- **MSI 自定义动作失败静默**：浏览/回写动作 `Return="ignore"` → `Return="check"`（失败即中止安装，不再悄悄继续）

### 其他

- `.gitattributes` 统一脚本/源码行尾（*.vbs/*.cmd/*.cs 等 CRLF），本地与 CI 构建产物校验和可跨环境复现
- 安全策略文档补充已知边界说明（PATH 解析、HKCU 自启归属）

## [0.2.0] - 2026-08-15

### 变更

- **托盘右键菜单自绘重构**：LayeredWindow 位图渲染（`UpdateLayeredWindow`，alpha 平滑圆角无锯齿）+ **16px 大圆角** + 内容垂直居中；**仅保留"退出"**一项（删除"显示 / 隐藏窗口"，窗口显示用左键单击托盘置顶）；红色电源图标（GraphicsPath 矢量绘制）+ 黑色"退出"文字，hover 淡红圆角（内缩同心）、弹出淡入动画（120ms）、点击外部/Esc 关闭
- **托盘菜单尺寸按 DPI 缩放**：物理像素 = 逻辑尺寸 × scale（DPI/96），150% 缩放屏上与 HTML 预览观感一致（此前按 96dpi 设计，高 DPI 屏上菜单/字体显小、间距被压缩像"遮挡"）
- **托盘菜单字体回退链**：Noto Sans SC Medium（思源黑体 500，原生"加粗一点点"）→ DengXian（等线，Win10/11 自带）→ Microsoft YaHei UI → 系统默认；等线/雅黑无中间字重时用伪粗体双画（Regular 字形 x+1 偏移，介于 Regular/Bold 之间），缺字体静默降级不崩
- **更新推送策略**：dsh-launcher 自身**普通更新不推送**，只有标记为**安全/重要更新**（GitHub Release body 含 `SECURITY` 或 tag 含 `-sec`）才托盘气泡提示（点击打开 Releases 下载页，气泡驻留 25s）；dsh（npm）有新版本仍提示（一键更新）
- **MSI 安装目录"浏览"按钮 → 现代化文件夹选择器**（Windows 10/11 新版文件夹对话框，IFileDialog）：Type-38 外部 exe（客户端进程弹窗，`FolderPicker.exe`）→ 所选路径写 `C:\ProgramData\dsh-launcher\picked.txt` → **DTF Type-1 托管 CA**（`WixToolset.Dtf.CustomAction` 5.0.2，net20 匹配 SfxCA 的 CLR 2.0，在 msiexec CA server 执行但其 `MsiSetProperty` 回写会同步回客户端 UI——实测日志 `PROPERTY CHANGE: Modifying INSTALLFOLDER`）→ 写安装目录属性。**输入框回显用双对话框交替**（ChooseFolderDlg ↔ ChooseFolderDlg2：MSI 控件静态绑定、属性变化不重绘，NewDialog 重建对话框后 PathEdit 重读属性）。关键坑：① SfxCA 选 stub 看 `$(Platform)`（默认 x86 → x64 msiexec 加载 193，需 `<Platform>x64</Platform>`）；② SfxCA 绑 CLR 2.0（net48 程序集 BadImageFormat，需 net20 目标）；③ `SetTargetPath` 参数必须展开成**属性名**（`[WIXUI_INSTALLDIR]`），字面路径报 MSI 2872；④ 取消按钮必须 `EndDialog Exit`（`Return` 在主 UI 序列会被当作正常结束 → 取消也被安装）
- **托盘/任务栏/资源管理器图标 → DeepSeek 蓝鲸鱼**（#4D6BFE，深浅背景都清晰）：托盘、任务栏按钮（WM_SETICON）、exe 图标（app.ico，文件夹/程序功能/快捷方式/固定）统一蓝色；**自绘标题栏鲸鱼保持主题**（深色→白、浅色→深）
- **自动检测并更新 dsh**：启动后异步检查 `@deepseek-ai/dsh`（npm registry）最新版，有新版本时**托盘气泡**提示，点击气泡确认后一键执行 `npm install -g @deepseek-ai/dsh@latest`（完成提示，需重启壳生效）；网络失败/无新版静默不打扰
- **版本更新检测接口**（`UpdateChecker`，已接入上述托盘气泡流程）：GitHub Releases（dsh-launcher 自身）+ npm registry（dsh）版本比较，语义化版本比较含单测；GitHub API 匿名限流、失败静默

### 修复

- **跟随窗口模式下关闭窗口服务不停（issue）**：`StopShellService` 的强制杀（`taskkill /f`）原先在后台 Task 里延迟 1.5s 执行——温和 `taskkill` 对无窗口的 node（wscript 隐藏启动）发 WM_CLOSE 无效，而壳退出后后台 Task 未及执行 `/f`，服务残留、端口仍监听。修复：温和终止 → **同步短等待（限时 &lt;1s）** → 未停则**在壳退出前同步强制 `/f`**，实测关窗即停、不卡关窗
- **托盘菜单透明不显示（0.1.32–0.1.34）**：重写时把 `CreateCompatibleDC`/`SelectObject`/`DeleteDC`/`DeleteObject` 四个 P/Invoke 误标为 `user32.dll`（实为 **gdi32.dll**）→ 每次渲染抛 `EntryPointNotFoundException` 被 catch 吞掉，LayeredWindow 位图永不生效、窗口全透明。修复 DLL 归属后实测渲染正常（日志 + 像素级验证）
- **托盘菜单位置被推出屏幕**：位置按"鼠标左上方"计算（`pt.X - 宽 + 12`），左侧竖排任务栏（托盘图标贴左边缘）时菜单直接越出屏幕。修复：屏幕边界自适应——左/上越界翻转到鼠标另一侧，仍越界贴工作区边缘
- **MSI 安装向导点"取消"/关窗口仍会完成安装**：自定义对话框的取消按钮误用 `EndDialog Return`——主 UI 序列（非模态）中 `Return` 被 MSI 当作"正常结束 UI（IDOK）"，安装继续执行；`Exit` 才是"用户取消退出安装"。所有自定义对话框（选项页、两份目录页）取消按钮改为 `EndDialog Exit`（欢迎页等 WiX 标准对话框本就是 Exit，故"上一步回欢迎页再取消"不装）
- **MSI 安装页"开机自启"说明文字被裁切**：复选框高度只有一行但文案两行（"…内存占用相对较大，非必要不推荐开启"）导致上下文字被遮挡——复选框调高为两行高度并显式换行，下方控件同步下移
- **服务停留模式每次打开被重置为跟随窗口**：根因是 profile 里安装的 dsh-launcher-lifetime 插件为**旧版**（`apply` 无条件把设置写回默认）——之前的同步因 PowerShell `Copy-Item 目录到已存在目录` 会**嵌套复制**（`lib\lib`）而从未真正覆盖旧文件；已清理嵌套目录并正确同步修复版（插件"文件已存在不覆盖用户选择"），hash 校验一致
- **系统任务栏图标在浅色主题下变黑**：Windows 11 任务栏按钮读取的是窗口小图标（ICON_SMALL），此前它跟随主题（浅色 → 深色鲸鱼），浅色主题下任务栏 logo 就变成黑色——修复：小图标固定鲸鱼（后随图标统一改为 DeepSeek 蓝，任何主题、深浅背景都清晰）；exe 资源图标（app.ico）同步更新

## [0.1.10] - 2026-08-14

### 变更

- **自绘标题栏（无边框窗口）**：彻底解决"主题切换后标题栏不刷新"（实测本机 DWM 属性切换后标题栏画面只有焦点变化才重绘，SWP_FRAMECHANGED / RedrawWindow / WM_NCPAINT 等全部无效）——像浏览器一样自己画标题栏：主题切换 = 改自绘颜色，**即时生效**。标题栏含主题鲸鱼图标、标题、MDL2 字形窗口按钮（最小化/最大化还原/关闭，带 hover 效果、关闭红色）、拖拽移动、双击最大化、右键系统菜单、边缘 8px 缩放、最大化限制在工作区（不遮任务栏）
- **标题栏 DPI 自适应**：标题栏高度/按钮/图标按 `DeviceDpi` 缩放（125%/150%/200% 都协调）；跨 DPI 显示器移动窗口（`DpiChanged`）自动重算布局；四周 1px 高对比边框替代阴影提升质感（带 WebView2 的无边框窗口无法获得系统阴影——WebView2 是不透明子窗口，与透明边距/扩展帧方案冲突）
- **配置位置迁移到 dsh 主目录**：settings.json / 启动轨迹日志 / 服务 PID 记录从 `%LOCALAPPDATA%\dsh-launcher` 移到 **`DSH_HOME\dsh-launcher`**（默认 `~/.dsh`，与 dsh 生态一致——dsh 自己的设置如 settings.yaml 也在 DSH_HOME；不散落在 LOCALAPPDATA，清理/迁移时跟着 dsh 走）。启动时自动迁移旧数据（保留设置值）并清理旧目录，卸载后无残留；WebView2 用户数据保持在 `%LOCALAPPDATA%\DshWeb`（浏览器标准位置，会话登录态随壳走）
- **主题以用户的选择为主**：壳读取 dsh 设置页的主题选择（`DSH_HOME/settings.yaml` 的 `ui-theme.preference`，dark/light/system），而不是只跟随系统；深色 → 白色鲸鱼 + 深色标题栏，浅色 → 深色鲸鱼 + 浅色标题栏，切换实时生效（FileSystemWatcher 即时 + 500ms 轮询兜底）
- **托盘交互优化**：**左键单击 = 窗口置顶显示**（开着就提到最上层并聚焦，最小化先还原，不会误关窗口）；**右键 = 只弹菜单**（不动窗口）；"显示 / 隐藏窗口"保留在右键菜单里供手动隐藏
- **托盘菜单样式 v2**：Win11 风格圆角浮层 + MDL2 图标（眼睛=显示/隐藏、电源=退出）+ 文字垂直居中（离屏渲染像素级验证图标与文字中心差 0px）+ 内容自适应宽度
- **双色小鲸鱼图标**：托盘图标与**系统任务栏图标固定白色鲸鱼**（深色背景看不清深色鲸鱼）；窗口标题栏小图标跟随主题（深色 → 白色鲸鱼，浅色 → 深色鲸鱼）
- **插件改名**：配套插件设置页"服务停留模式"更名为 **"Node 服务驻留"**（文案点明 node 服务与托盘的关系）；侧边栏导航图标不再用默认齿轮（本机定制了 dsh 包的 `navIcon` 映射，dsh 升级后需重新应用，见插件 README）
- README 增加"与 dsh 插件联动"章节（安装命令、设置文件位置、模式切换入口说明）

### 修复

- **自绘标题栏遮挡内容**：Dock 布局下 WebView2 从 y=0 填充盖住标题栏区域，内容顶部被挡——改为手动 Bounds + Anchor（内容从标题栏下方开始、四边跟随窗口缩放），实测窗口尺寸任意调整布局正常
- **最小化后托盘单击"点不回来"**：最小化窗口忽略 `Activate()`——单击托盘先 `SW_RESTORE` 还原再激活
- **深色模式下窗口/任务栏鲸鱼"消失"**：主题与图标配色逻辑写反（深色主题误用深色鲸鱼）——深色主题用白色鲸鱼、浅色用深色鲸鱼
- **主题解析误读其他配置段**：`ui-theme.preference` 的解析严格限定在 ui-theme 段内，不再误读其他段的同名字段
- **关闭窗口卡 1-2 秒**：① 不再显式 Dispose WebView2（进程退出后浏览器子进程自动清理）；② 停服务改为异步 + 内存缓存服务 PID（关窗不再跑 netstat）——实测关窗到进程退出约 100ms
- **服务模式"常驻"被重置**：设置读取兼容旧路径（`%LOCALAPPDATA%`，旧插件写入位置）——新位置读不到时回退旧值并迁移；插件侧同时修复"每次启动无条件写回默认"（见插件 CHANGELOG）
- **托盘唤起后立即重载页面可能崩溃**（实测 0xc0000005 / .NET Runtime internal error）：隐藏→恢复→立即 `Reload()` 与 WebView2 的可见性处理存在竞态；改为延迟 500ms 且窗口再次隐藏时放弃并留待下次恢复
- **崩溃后服务残留无人管理**：壳拉起的服务 PID 记录到数据目录；下次启动若发现该 PID 仍在监听，自动接管（"跟随窗口"关窗时一并停掉），避免进程崩溃后 node 服务永久残留占内存
- **插件每次启动把服务模式重置为默认**：插件 `apply` 只在无显式配置且文件缺失时写默认值，用户通过设置页的选择不再被覆盖

## [0.1.9] - 2026-08-14

### 变更

- **托盘菜单样式打磨**：去掉系统默认样式的"老气感"（左侧图标留白、跟随深色主题变黑、系统主题色 hover）——改为简洁白底 + 1px 浅灰边框 + 浅灰 hover + 深色文字，字体微软雅黑 9pt，菜单项留白舒展；保留"显示 / 隐藏窗口"与"退出"两项
- **默认省内存**：未配置服务模式时默认"跟随窗口"（关窗即停 dsh 服务，下次启动自动拉起；想常驻在插件设置里改）；MSI 安装向导的**开机自启默认不勾选**，勾选框注明"内存占用相对较大，非必要不推荐开启"
- **托盘图标始终显示**（任何服务模式）：此前只在"托盘驻留"模式创建，导致默认"常驻"模式下用户找不到"服务模式"切换入口；现在启动即有托盘（小鲸鱼），右键可随时切换模式/退出（常驻模式托盘退出只退壳、服务保留）；托盘创建失败不影响壳主流程
- 托盘右键菜单瘦身：移除"服务模式"子菜单（改为插件在 Harness 设置页配置），保留显示/隐藏与退出
- 壳支持环境变量 `DSH_WEB_PORT` 指定**壳托管的服务端口**（3080 被占用时可用；`DSH_WEB_URL` 仍为外部托管语义）：壳按该端口拉起 dsh 服务（start-dsh.vbs 支持 `DSH_PORT` 透传），单实例锁、就绪探测、关窗停服务都按该端口
- 启动轨迹日志：`%LOCALAPPDATA%\dsh-launcher\shell.log` 记录壳的关键决策点（单实例、端口探测、服务拉起、就绪判定、窗口显示），启动异常时可直接查看定位

### 修复

- **从托盘唤起窗口一片空白**：
  1. **托盘驻留模式点关闭按钮必然白屏**：`FormClosing` 先销毁 WebView2（`web.Dispose()`）再判断是否拦截到托盘——拦截后窗口虽隐藏、控件却已销毁，唤起时只剩空白。修复：托盘驻留拦截判断移到 WebView2 销毁之前（拦截时保留控件，真正退出时才销毁），已实测"关闭→托盘→唤起"内容正常
  2. **托盘隐藏期间渲染/GPU 进程崩溃 → 唤起白屏**：崩溃处理改为记录标志，隐藏状态下不立即 Reload（无效），恢复窗口时兜底重载页面（含 GPU 进程崩溃，此前未处理）
  3. **长隐藏（>5 分钟）渲染进程被系统回收**（无崩溃事件）：恢复窗口时强制重载页面兜底
- **首次启动要二次点击才能开窗（根因已定位并修复）**：
  1. **根因**：冷启动流程先创建了启动状态窗（IWin32Window），服务就绪、状态窗关闭后 Main 才调用 `Application.SetCompatibleTextRenderingDefault(false)` → 抛出 `InvalidOperationException` → **进程静默崩溃**（Windows 错误报告，无任何提示）→ 主窗口永远不出现。用户看到状态窗消失后"没反应"，再点一次——此时服务已在跑、跳过状态流，才轮到正常的初始化顺序 → 开窗成功。表现为"要点击两次"。修复：`EnableVisualStyles` + `SetCompatibleTextRenderingDefault` 移到 Main 最前面（任何窗口/控件创建之前），已在两台路径实测（冷启动 状态窗→就绪→自动开主窗口，无崩溃）
  2. 就绪判定改为"端口可连 + HTTP 有响应"（此前端口一开就判定成功，但 dsh 前端 HTTP 还要数十秒才就绪，探测过早失败 → 壳退出 → 服务后台继续启动 → 用户二次点击才成功）
  3. **端口已开但 HTTP 前端未就绪时也显示状态窗等待**：此前直接开窗会白屏数十秒（用户以为没反应而多点一次）；现在统一等 HTTP 就绪再开主窗口
  4. 状态窗标题不再与主窗口同为 "DeepSeek Harness"（改为"dsh-launcher 启动中"）：二次点击时单实例逻辑按标题只会找到真正的主窗口并等待其出现，不会把状态窗误当主窗口聚焦（表现为"点了没反应"）；文案注明"完成后会自动打开窗口，请稍候"
  5. 日志错误标志（npm ERR / EACCES / ECONNREFUSED 等）判定加 **15 秒宽限期**：启动过程中的良性告警也会命中这些关键词，此前会立即误判"启动失败"退出；现在宽限期内 HTTP 就绪仍算成功，只有持续失败才报错
  6. **启动日志按端口隔离**（3080 用 `.dsh-web.log`，其他端口用 `.dsh-web.&lt;port&gt;.log`），且被运行中的服务锁定时 vbs 回退到 `%TEMP%`：此前 `.dsh-web.log` 被运行中的 dsh 服务（stdout 重定向）锁定时，vbs 的 `echo > 日志 && dsh web >> 日志` 整条失败（`&&` 串联），**服务根本起不来** → 状态窗永不开窗
  7. 启动轨迹日志：`%LOCALAPPDATA%\dsh-launcher\shell.log` 记录壳的关键决策点（单实例、端口探测、服务拉起、就绪判定、窗口显示），启动异常时可直接查看定位（本轮排障即靠它逐条定位）
- **开机自启默认不勾选未生效**：MSI 条件中非空字符串 `"0"` 被当作 true，`NOT AUTO_START_OPTION` 对默认值不生效（默认仍安装了自启）；改为显式数值比较 `AUTO_START_OPTION <> 1`（实测默认不装、勾选才装）
- **孤儿自启清理**：per-machine 提权卸载跳过 per-user 组件时会残留 HKCU Run 自启项，壳启动时检测其指向的 `start-dsh.vbs` 不存在则自动删除

## [0.1.8] - 2026-08-14

### 修复

- **显示缩放下字体/图标模糊（[issue #2](https://github.com/Ruler4396/dsh-launcher/issues/2)）**：壳未声明 DPI 感知，Windows 在 125%/150% 缩放下对 WebView2 内容做位图拉伸导致模糊（浏览器因为 Per-Monitor DPI aware 而清晰）。修复：Main 第一行调用 `SetProcessDpiAwarenessContext(PerMonitorV2)`（WinForms 的 SetHighDpiMode 在部分环境下因先前的弹窗而失效，改用 user32 直接调用），运行时验证进程 DPI awareness = 2（per-monitor）；主窗口按初始 DPI 放大，保持逻辑大小不缩水

## [0.1.7] - 2026-08-14

### 新增

- **启动依赖预检**：壳在需要自动拉起 dsh 服务前快速检测 Node.js，缺失时立即弹窗提示安装（不再静默等待超时才报"服务不可用"）；WebView2 初始化失败也有明确提示（此前会静默无窗口）
- **服务启动状态窗**：自动拉起服务期间显示"正在启动 dsh 服务…首次运行需要下载组件"的进度提示（可取消）；首次 npx 下载不再是静默等待——超时（3 分钟）会区分"下载较慢/网络问题"并指引日志 `%USERPROFILE%\.dsh-web.log`
- **首次下载差错控制强化**：等待期间持续监控启动日志，出现明确错误（npm ERR、EACCES/ENOSPC/ETIMEDOUT、无 npx、模块缺失等）立即结束等待；失败/超时弹窗**直接附带日志尾部**展示真实原因；端口就绪后额外 HTTP 探测确认是 dsh 服务（防端口被其他程序占用）；页面加载失败也有明确提示（不再白屏静默）
- **服务停留模式（托盘 + 生命周期）**：壳读取 `%LOCALAPPDATA%\dsh-launcher\settings.json` 的 `serviceLifetime`（由配套插件或托盘菜单写入）：`0` 常驻（默认，服务一直运行）/ `1` 托盘驻留（关窗最小化到托盘，托盘"退出"才停服务）/ `2` 跟随窗口（关窗即停服务并退出）。只停壳本次会话拉起的服务（外部托管/用户手动启动的不动）；托盘图标双击切换窗口、右键菜单含**服务模式子菜单**（即时切换）与退出

## [0.1.6] - 2026-08-14

### 变更

- MSI 改为**系统级安装（per-machine）**：安装/卸载会弹一次 UAC 管理员确认，默认装到 `%ProgramFiles%\dsh-launcher`（向导仍支持自定义目录，如已有的 E:\ 目录）；注册表、快捷方式改为 HKLM / 公共桌面 / 公共开始菜单，卸载自动清理
- **旧版本自动清理（安全版）**：壳程序启动时检测机器上是否还有其他版本的 dsh-launcher（per-user 的 0.1.0–0.1.5 等），检测到则提示用户一键提权卸载旧版（提权卸载不会触发 Config.Msi 1926），避免多版本共存；当前运行的版本通过安装时写入的 `HKLM\Software\dsh-launcher\CurrentProductCode` 识别，永远不会被误卸。**识别用固定 UpgradeCode 精确匹配**（读取缓存 MSI 的 UpgradeCode，与 `{3B29D055-...}` 一致才算本产品）——其他恰好同名的软件不会被误清理；弹窗让用户最终确认
- **孤儿快捷方式自愈（安全版）**：per-user 旧版被（提权）卸载后，其用户级开始菜单/桌面快捷方式可能残留（MSI 提权卸载跳过 per-user 上下文组件），壳每次启动自动清理**目标确为 DshWeb.exe** 的快捷方式（读取 .lnk 目标验证），用户自行创建的同名快捷方式（指向其他程序）不受影响
- **应用图标（小鲸鱼）**：壳 exe 编译自带图标资源（此前 exe 无图标，快捷方式与"设置 → 应用"都显示系统默认图标）；MSI 安装的快捷方式与卸载条目现在都显示小鲸鱼图标（`ARPPRODUCTICON` + 显式 `DisplayIcon` 注册表值）

### 修复

- **根治装→卸报错 1926/"无法设置文件…Config.Msi…的安全权限，错误: 5"**。根因：Windows Installer 在**卸载**期仍会创建回滚文件（.rbf）到安装盘根目录的 `Config.Msi`，并以用户身份对其设置安全，而该目录 ACL 由 MSI 服务硬编码为仅 SYSTEM/管理员（任何盘根/目录 ACL 都无法绕过，已实测）；非提权用户（含 UAC 过滤的管理员）在自定义 ACL 的磁盘（如本机 E:\）上必然失败。修复：per-machine 提权后，卸载事务以管理员身份匹配 `Config.Msi` 的 Administrators ACL，不再报错；另保留安装期 `DISABLEROLLBACK=1` 作额外保险。默认目录（C:）与非提权路径本无此问题
- 从 0.1.5（per-user）升级：本机实测可自动升级（RemoveExistingProducts）；标准机器上 per-user 旧版注册在 HKCU、per-machine 新版找不到时，新版启动后会自动提示"检测到旧版本"，一键提权卸载旧版（无需手动清理，也不再有 1926 报错）

> **升级提醒 / For users of older versions**
> 0.1.6 修复了旧版本（per-user，0.1.5 及更早）在部分磁盘上"安装后立即卸载报错 1926/错误 5"的问题，并会自动清理机器上残留的旧版本，**建议尽快更新**。
> 旧版本用户如果之前把 dsh-launcher 装到了 E:\ 等自定义目录，卸载旧版时可能看到 1926/"无法设置文件 Config.Msi 的安全权限，错误 5"提示——这是 Windows Installer 对回滚文件的系统级行为，**报错后产品仍会被正常删除**，不影响结果；更新到 0.1.6 后，新版首次启动会检测到旧版本并提示一键提权卸载（不再有 1926 报错）。如果升级后发现"设置 → 应用"里有两个 dsh-launcher，直接用新版弹出的提示清理即可。

## [0.1.5] - 2026-08-14

### 新增

- 壳支持环境变量 `DSH_WEB_URL` 覆盖目标地址/端口（免重建）；设置后视为外部托管服务、不再自动拉起；单实例锁按目标端口隔离

### 变更

- MSI 安装向导重做：去掉老式"功能树"下拉（将安装在本地硬盘上/整个功能…/功能将在需要时安装/整个功能将不可用 + 重置/磁盘使用量按钮），改为简单向导 + **三个勾选框**（开机自启 / 桌面快捷方式 / 开始菜单快捷方式），卸载快捷方式始终安装
- "选择安装目录"页重新设计为 **Segoe UI 现代风格**：简洁布局 + 直接输入/粘贴路径（默认 `%LOCALAPPDATA%\dsh-launcher`）。注：系统原生文件夹浏览按钮因 Windows Installer 自定义动作在本环境的稳定性问题暂不提供，路径输入完全可靠
- MSI 向导支持**自定义安装目录**
- 卸载安全：卸载仅删除本应用文件，目录仅"空"时移除；与 DeepSeek Harness 等共用目录时其他内容不受影响（已实测验证）
- `uninstall-autostart.cmd` 额外清理旧版 `dsh-autostart.vbs` 自启项

### 修复

- 自动播放被 WebView2 静默拦截（当前 SDK 不触发 Autoplay 权限事件）→ 主窗口与插件弹窗共享同一 WebView2 环境并注入 `--autoplay-policy=no-user-gesture-required`，声音类插件可用
- 打包脚本末尾清理对缺失目录容错

### 测试

- 隔离沙盒端到端实测（全新 `DSH_HOME` + 最新 dsh 0.1.0-rc.6 + dsh-notification / dsh-web-ui-notify 双通知插件共存）：通知权限、剪贴板、自动播放（静音与非静音）、同源弹窗子窗口、下载落盘与同名避让、单实例、双插件共存全部通过；确认 WebView2 会屏蔽 `--remote-debugging-port`（外部 CDP 不可用，测试改用自建测试页 + fetch 回报）
- MSI 向导 UI 自动化验证：三个勾选框取消勾选后安装（自启/桌面/菜单快捷方式均不装，卸载快捷方式保留）、默认全勾选安装、自定义安装目录、与第三方文件共用目录时卸载不误删、卸载零残留均通过

## [0.1.3] - 2026-08-13

### 新增

- MSI 安装向导（WixUI，中文界面）：安装时可勾选是否开机自启；开始菜单新增"卸载 dsh-launcher"快捷方式
- Release 说明自动附带"安装与卸载"段落（MSI vs ZIP 区别移至 Releases 页说明）

### 变更

- README 精简为新手向短文档，详细内容移至 `docs/DETAILS.md`
- 打包脚本健壮性：发布产物完整性校验、自动安装 WiX UI 扩展

## [0.1.2] - 2026-08-13

### 修复

- `start-dsh.vbs` / `start-dsh.cmd`：`dsh` 不在 PATH 时自动回退 `npx -y @deepseek-ai/dsh web` 拉起服务。此前若只通过 `npx` 使用 dsh 而未全局安装，静默自启会失败，表现为“必须先手动跑 `npx @deepseek-ai/dsh web`，壳窗口才会弹出来”；`%USERPROFILE%\.dsh-web.log` 首行现在会写明实际使用的启动方式

### 测试

- 新增 `tests/DshShell.Tests` 单元测试（xunit，55 用例）：弹窗分类、权限策略、下载文件名推导与清理
- 新增 `scripts/test.ps1` 集成测试：脚本静态回归断言、uninstall 行为测试、可选冒烟测试（窗口/单实例）
- CI 增加 `dotnet test` 步骤
- 修复 `blob:`/`data:` 下载文件名问题：不再取随机 UUID 尾段，改为时间戳 + MIME 扩展名
- 修复文件名清理：Windows 保留设备名（`CON`/`NUL`/`COM1` 等，含带扩展名形式）与结尾点/空格现在会被正确处理

## [0.1.1] - 2026-08-13

### 新增

- 壳应用自动授权桌面通知与剪贴板权限（WebView2 `PermissionRequested`），支持 dsh-notification 等通知插件；麦克风/摄像头保持默认拒绝（隐私）
- 权限策略扩充：自动放行自动播放 / 多文件下载 / 持久存储，兼容声音类与批量导出类插件
- 同源弹窗（`window.open()`）改为新建轻量壳窗口：保留会话状态，主窗口不再被导航走；外部链接进系统默认浏览器；`blob:`/`data:` 等保持 WebView2 默认
- `blob:` 无扩展名下载按 MIME 类型自动补扩展名
- WebView2 初始化抽成共用方法（主窗口与弹窗行为一致）
- 下载处理：文件自动保存到系统“下载”文件夹（自动避开同名文件），完成后用默认程序打开
- 渲染进程崩溃/无响应时自动重载页面（10 秒节流，避免死循环）
- 壳应用单实例保护：重复启动自动聚焦已开窗口，不重复创建 WebView2 进程
- 关闭表单自动填充与密码保存，降低后台开销；保留 F12 开发者工具

### 修复

- 卸载脚本 `uninstall-autostart.cmd` 改为删除启动文件夹中的自启项与指向 `DshWeb.exe` 的桌面快捷方式（此前误删不存在的计划任务）
- `dsh-web.cmd` 改为从脚本同目录启动 `DshWeb.exe`，并处理 `start-dsh.vbs` 缺失的情况
- 发布包现在包含全部运行时脚本（`start-dsh.vbs` / `start-dsh.cmd` / `dsh-web.cmd` / `uninstall-autostart.cmd`），部署目录自包含

### 文档

- README：补充 .NET Desktop Runtime 10 运行依赖与安装方式；更新目录结构与构建说明；精简版本兼容性等表述
- README 改为完整中英双语（中文 + English 各一份完整版）

## [0.1.0] - 2026-08-13

### 新增

- WebView2 轻量壳应用（单文件约 1MB）：独立窗口打开 dsh Web UI，替代完整浏览器
- 静默开机自启：`start-dsh.vbs` 无窗口启动服务，无需管理员权限
- 一键入口 `dsh-web.cmd`：检测端口 → 自动拉起服务 → 打开壳窗口
- 壳应用自动拉起：服务未运行时自动启动并等待就绪（最长 90s）
- 日志落盘：服务输出写入 `%USERPROFILE%\.dsh-web.log`
- WebView2 用户数据隔离：存放于 `%LOCALAPPDATA%\DshWeb`，不污染程序目录
- 卸载脚本 `uninstall-autostart.cmd`
- GitHub Actions CI：自动构建 Windows 发布包，tag 推送自动发布 Release
- 打包脚本 `scripts/build-release.ps1`

### 文档

- README（中文为主 + 英文简介）：快速开始、内存对比、目录结构、FAQ
- 贡献指南、安全说明、行为准则、Issue/PR 模板
