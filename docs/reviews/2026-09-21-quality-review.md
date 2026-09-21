# dsh-launcher 全量代码质量审查（2026-09-21）

> 审查性质：**先只审查、不改代码**；经用户裁决后按 §5 顺序进入修复。
> 方法：6 分区并行深审（组合根/状态机、Managers+Domain、异常透明性、UI/Win32/WebView2、测试+CI+安装器、ShellLogic 纯逻辑层），主会话对每条最重发现 `sed` 读原文复核。
> 证据强度标注（"声称强度必须匹配测量强度"）：✅=主会话读原文复核属实；◇=子代理代码证据、主会话未复现；○=需真机/运行时定论。
> 对照铁律：`AGENTS.md`、`docs/00-ARCHITECTURE-GUARDRAILS-MANDATORY.md`、`docs/TESTING-GUARDRAILS.md`、`docs/ARCHITECTURE-DEBT-LEDGER.md`。
> 基线（本轮实测）：快线 `Category!=RealOS` **1309 passed / 0 failed / 31s**；src 编译零警告；工作树干净（除 `dist-release/`、`.zcode/`（已 ignore）与游离 `package-lock.json`）。

## 0. 执行摘要

架构纪律是真实的：状态文件全部 AtomicWrite、src 内无 `cmd.exe /c` 包装、Manager 依赖方向未恶化、台账 21 条中 17 条与现状一致。

本轮的系统性发现一句话：**最大的风险不在"没有闸"，而在"闸的正则形状与违例形状已分叉"——G5/G6/G10/G12/G13 各自把真窟窿当成允许项/字面合规放行**。行为缺陷与闸盲区成对出现（例：DPI 塌 0×0 的洞恰好落在 G10"硬零"的 `96f` 字面量之外）。

无 P0：未发现 fail-closed 拒绝启动路径。

## 1. 发现清单总表

### P1（行为缺陷）

| ID | 级别 | 位置 | 一句话问题 | 证据 |
|---|---|---|---|---|
| N1 | P1 ✅ | `Program.cs:289` | 单实例 mutex `using var` 方法返回即释放句柄 → 二实例走完整启动流程，restore+E1009 分支永不触发；仅剩 WebView2 目录锁 E1006 部分兜底。测试只钉名字格式（F21），无持有期用例（双开后果○） | `using var mutex = new Mutex(true, …SingleInstanceMutexName(Target.Port), out var firstInstance);` |
| N2 | P1 ✅ | `Program.cs:279` vs `:186` | `RegisterCrashHooks()` 在 stage 3 才注册，而 `--diagnose/--ui-selftest/--ui-probe` 在 :186 已 return → stage 1/2 与 CLI 路径抛异常时**无 E9001 日志也无弹窗**——正是文档声称已根治的"双击后无声消失" | `InitializeProcessEnvironment(); if (HandleCommandLineArgs(args)) return; InitializeCoreDataAndLogs();`（钩子在最后者内部） |
| N3 | P1 ✅ | `Lifecycle/SafeModeLifecycle.cs:211-218` | catch 做了 StopMonitor/Deactivate/撤横幅，唯独**不投 `SafeModeEntryFailed`** → 状态机永久滞留瞬时态（其 :142 注释自证后果）；:138 又忽略 `TryFireLifecycle` 返回值，被否决后照样建 profile/停服/拉起（违反核心约束一.1）。同模式：`ServiceRestartCoordinator.cs:163`、`UpdateRollbackCoordinator.cs:134` ◇ | catch 块全文仅 4 语句，无 TryFire |
| N4 | P1 ✅ | `tests/DshShell.Tests/Outcomes/SafeModeE2EOutcomes.cs:121-127` | **可红性铁律点名的事故复发**：路径守卫 `if (File.Exists(vbsPath))` 使 vbs 断言从未执行（tests 输出目录无 vbs），且真 `scripts/start-dsh.vbs` 里 `--safe-mode`/`DSH_SAFE_MODE` grep=0 命中——**真跑必红**；文件头 :27-31 注释还自称"此断言已删"。用例主体是 Set/Get 环境变量自比+字符串自比（断言自己型）。同文件 :79-84 与 `SafeModeSandboxOutcomes.cs:141-146` 为零 Assert 块（且路径指真实 `~\.dsh`，环境泄漏） | `grep -c -- "--safe-mode\|DSH_SAFE_MODE" scripts/start-dsh.vbs` → `0` |
| N5 | P1 ✅ | `Program.cs:607`、`Win32/WindowGeometry.cs:114` | DPI 换算绕开 `ShellLogic.DpiScale`：两处写 `/ 96.0`，而 G10 正则只扫 `/\s*96f` → **机器闸结构性看不见**。`DeviceDpi==0`（D8 批次立规的坏驱动/RDP 现场）时主窗塌 0×0、标题栏塌没。台账第 8 条"src 裸 `/96f` 归零"仅字面成立 | `var scale = (double)form.DeviceDpi / 96.0;` / `var titleH = (int)Math.Round(32.0 * dpi / 96.0);` |
| N6 | P1 ✅ | `Program.cs:1352` | "退出安全模式"的 `_ = Task.Run` 首 await 不在 try 内，全仓无 `TaskScheduler.UnobservedTaskException` 钩子（grep 证实）→ 重启失败零留痕、标题栏永停"正在退出安全模式"。同形状其余 5 处（1657/1804/1978/2610）都有 try，唯此处没有 | `var outcome = await RestartCore!.RestartAsync("exit-safe-mode");` |
| N7 | P1 ◇ | `tests/.../RealOs/DshUpdatePipelineRealTests.cs:94,:307` | RealNet 用例（Trait 只有 `RealNet`）被快线 filter `Category!=RealOS` **放行**，env≠1 时裸 `return` 仍计入通过数 → 条数对账口径被掺水；realos 层有 `&Category!=RealNet`，快线侧没有（影响面○需 --list-tests 对账） | `if (env != "1") return;` |

### P2（择重）

| ID | 位置 | 一句话问题 | 证据 |
|---|---|---|---|
| N8 ✅ | `ShellLogic.cs:2276-2291` | netstat 回退违反三必须：同步 `ReadToEnd()` 排在 `WaitForExit(3000)` **之前**（流挂住则超时形同虚设，调用线程含 UI 侧可无限阻塞）、超时不 Kill、stderr 未重定向、外层空 `catch {}`。**G12 棘轮把它当"允许的 2 处手写"藏住** | `var output = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);` |
| N9 ◇ | `Managers/ProcessRunner.cs:234-326` | `RunPnpmInstall` 比台账 7 更糟：`WaitForExit(600000)` 排在逐行 `ReadLine()` 之后 → 输出流挂住时 10 分钟兜底**永不生效**；任何路径无 `Kill(entireProcessTree)`；未退出读 `ExitCode` 抛异常被 :321 吞成 false | 台账 7 只记"无 ct 形参" |
| N10 ◇ | `scripts/test.ps1:725`（G6） | G6 只数**同行** `catch {}`；全量扫描：多行"只有注释"的真空 catch **153 处** + `catch{return false/null/0;}` **25 处** ≈ 178 处在闸外（docs/00 三.1 明令禁止后一形状）。最重四处：`RuntimeResolver.cs:275`（VerifyChecksum 任何异常→报 **E1004"校验和不匹配"**，把网络问题写成供应链完整性问题）、`:295`（解压真因丢弃只报猜测文案）、`Windows/LegacyUpgradeCleanup.cs:78/90`、`Managers/ServiceLifecycleOps.cs:152` | 计数为子代理实测方法：全 src 扫描 |
| N11 ✅ | `scripts/start-dsh.vbs:60-83` + `DshShell.csproj:48/51` | 因果地图点名的身份割裂**仍在分发**：回退链 `cmd /c where dsh`→硬编码 npm `.cmd` shim→npx，**无 SelfContained `runtimes\` 级**（DshDiscovery 的最高优先级），另用 `cmd /c echo >>` 向 UTF-8 日志写 GBK。生产壳已不调用它（Program 侧 grep 证实） | 见 N11 复核输出 |
| N12 ✅ | `Managers/AppEnvironment.cs:204` | 迁移路径裸 `File.WriteAllText` 写 settings.json + 空 catch；同文件 :176 用 AtomicWrite（自引 F22 教训）——自相矛盾，违反核心约束二.2 | `File.WriteAllText(settingsPath, json); File.Delete(legacy);` |
| N13 ◇ | `Program.cs:2662/2667`、`:1826-1830` | 错误码契约两破口：`NotifyUpdateApplyFailed` Error 级模态正文**无 [E####]** 且绕过 `ShowError`→不落结构化日志（诊断包 `SummarizeErrors` 只认 code 字段），应为 E4002；"两级 profile 失败"文案里裸拼 E1011 而日志实记 **E1010**（`SafeModeLifecycle.cs:141`），且 `TryEnter` 在 SessionShuttingDown 分支返回 false 时**无任何 Error 记录**照样弹"启动验证失败" | — |
| N14 ◇ | `ShellLogic.cs:160-166` | `WebViewPolicy.ClassifyPopup` 只判 host∈{127.0.0.1,localhost} **不判端口** → 本机任意端口的服务可开"壳内可信窗"（UI 欺骗面；注释与实现的同源承诺需再对一遍消费者） | `uri.Host is ("127.0.0.1" or "localhost")` |
| N15 ◇ | `Lifecycle/LauncherLifecycle.cs:75` + `LauncherApp.cs:411-418` | 状态机 `_state` 无锁非 volatile；CanFire→Fire 是 TOCTOU；安全模式阶梯（Task.Run）与 UI 线程 `RequestShutdown` 并发时可抛 `InvalidOperationException`，违背 TryFire"不抛"契约 | — |
| N16 ○ | `Windows/DshShellForm.cs:192-197` | `WM_NCACTIVATE` 每次激活/失焦都在 WndProc 内同步 `SWP_FRAMECHANGED`；核心约束四.2 的去重状态机 `_lastWindowState` 实测只覆盖 OnResize 一条路（NC 激活与 OnShown 两条没去重）→"去重"只有 1/3 调用点生效 | — |
| N17 ◇ | `Managers/WindowManager.cs:174-201` | 托盘唤回先无条件 `RecoveryNeeded=false`，500ms 后仅当 `CoreWebView2 is not null` 才 Reload，否则不复位不留痕 → 隐藏期崩溃后唤回**永久白屏**（台账 5 同症状的第二条路径） | — |
| N18 ◇ | `Managers/WebViewManager.cs:306-336` | `NewWindowRequested` 处理器 `async void` 且只有 try/finally 无 catch：弹窗 `EnsureCoreWebView2Async` 抛 → 逃逸到 UI 线程=未处理异常；且 `e.NewWindow` 未赋值时 deferral 已完成，行为未定义 | — |
| N19 ◇ | `Chrome/CustomTitleBar.cs:157-161 vs :479` | npm 构建期 ~30fps `_marqueeTimer` 不随控件 Dispose；关窗后 Tick 继续打已释放控件（Timer 泄漏可证伪；是否抛 `ObjectDisposedException` ○） | — |
| N20 ◇ | `Managers/WebViewManager.cs:107-136` | 崩溃节流/导航自愈静态**不经 ReferenceEquals 门控**——台账 4 之外的第四个跨窗共享（`_navSucceededSinceFailure` 注释自称"主窗级"实为进程级）；现为潜伏污染（`ArmNavigationRetry` 目前只对主窗调用） | — |
| N21 ○ | `Managers/F11LowLevelHook.cs:74-77` | WH_KEYBOARD_LL 回调里同步执行窗口切换（→布局→WebView2 Resize）；超 `LowLevelHooksTimeout` 被 Windows **静默摘钩**（此后物理 F11 永久失效且无报错）。正解 `BeginInvoke` 投递。另 `:55` 写死 `GetModuleHandleW("DshWeb.exe")`，改名即装不上（P3） | — |
| N22 ◇ | `Program.cs:119` + `:1752` | 诊断链自相矛盾：各 Manager 的阶段证据全走 `Trace→Logger.Info`，而启动失败自动导出用 `minLevel:Warn` → `log-warn.txt` 恰好不含过程证据；`Logger.cs:123-140` fallback 实现属实但自身失败静默、`DiagnoseExport` 不采集 fallback 文件 | — |
| N23 ◇ | `.github/workflows/ui-test.yml` | 触发只盯 `master`、缺 `v[0-9]*.*[0-9]*`（违反核心约束五.7 口径统一）；`checkout@v4`/`setup-dotnet@v4` 可变 tag，而 build.yml 全 SHA pin（供应链口径不一） | 子代理 `gh run list` 实测四条线触发真实 |
| N24 ✅ | `installer/PrereqCheck/PrereqCheck.cs:9` | 文档头仍写"退出码 2=缺失（中止安装）；3=用户取消"——这两个形态已被真机证伪并修掉（product.wxs 注释在案）；按文档头实现会把"程序包有问题"弹窗焊回来 | 正文常量仅 `Continue=0`/`UserCancelled=1602` |
| N25 ◇ | `Domain/DshUpdateManager.cs:698`、`StagedUpdate.cs:142` | 台账 13 的"承诺与现状一致"被证伪：tarball 文件名 `deepseek-ai-dsh-{ver}.tgz` 两处硬编码，未经 `DshDiscovery.PackageScope/ShortName` 派生——G13 只拦 `@`/斜杠形态故不红；scope 变更时 pack 与 LocateTarball 双双漏改 | 见 N11 同族 |
| N26 ✅ | `ShellLogic.cs:1563,:1582` | 自称纯的文件读时钟且 UTC/本地混用：`SuggestDownloadName` 用 `DateTime.UtcNow`、`SanitizeFileName` 用 `DateTime.Now` → 契约不可复现 | — |
| N27 ◇ | `Lifecycle/ServiceRestartCoordinator.cs:103-131` → `Program.cs:1624-1651` | 后台线程直弹模态：escalate/showError 在进程事件/线程池线程上 `form.Activate()+MessageBox.Show`，无 BeginInvoke（对照：HandleBootHealthFailed :1573 有封送）；是否出实际 UI 故障 ○ | — |
| N28 ◇ | `Program.cs:2218-2272` | 组合根内联业务事务（G9 拦不住的新写法）：`PromptApplyRestart` 停服→npm 进程→ClearPending→RestartAsync 整条 55 行在 Program.cs，违反第一原则.1；另有纯决策内联（:2731 手写 YAML 解析、:1595 裁决前缀判定）违反第一原则.3 | — |

### P3（一句话清单）

`RunTaskKill:2355-2361` 重定向双流但从不读取（taskkill /T 大树 >4KB 管道堵，有超时 Kill 兜底）◇；`DiagnoseExport.cs:144-145` 裸 `node`/`npm` 靠 PATH ◇；`RuntimeResolver.cs:218` 下载失败泄漏空 tmp 目录 ◇；端口枚举 IPv4-only（`PidByPortViaTcpTable` 仅 AF_INET、回退 `-p tcp`；现靠 `--host 127.0.0.1` 未触发）○；`ShellLogic.ProcessManagement`(261 行)/`BootGuard`(340 行) 双双超过文件头自订 250 行拆分阈值 ◇；G1b 的 `new\s+ProcessStartInfo` 匹配不到全限定名，且模式族不含 `File.ReadAllText/WriteAllText`/`Logger.*`/`Thread.Sleep` → 新增这类原语可无声驻留 ◇（台账"11 处"口径本身没错，覆盖面比宣称窄）；G5 正则只匹配 bool/int/long/DateTime → `_pendingUpdate/_pendingLatest/_pendingLocal/_applyRestartPendingVersion` 4 个控用户可见流转的枚举/字符串标量在闸外 ◇；台账 6 位置过期（第 6 份手写采集实为 `PidByPortViaNetstat`，NpmHelpers 内已无进程代码）◇；台账 17 名单漏 `DSH_TEST_SPLASH_DELAY_MS`（`LauncherApp.cs:179-183` 整段 `return true` 跳过生产流水线——"替换结论"最彻底一例）◇；台账 15 引用行号漂移（604→~616）◇；`PathPolicy.IsSafeVersionSegment` 白名单放行 `CON/NUL/COM1-9/LPT`，现成 `ReservedNames:1509` 未复用 ◇；`VersionPolicy` 把 `1.0.0-0-g<sha>` 判新于 `1.0.0` ○；`ResolveNpmCmdPath/HasExecutableOnPath` 生产端零调用仅测试续命 ◇；插件弹窗不随运行期主题切换（`WindowManager.cs:240-311` 只持主窗）◇；`BootHealthMonitor.cs:197-201/640-653` 重挂不 Dispose 旧句柄、`_cts`/`SessionCts` 泄漏、httpProbe 3s 一 new 永不 Dispose ○；`UiSelftestProbe.cs:59` 用逻辑像素 WorkingArea 比物理 Bounds，与 `DisplayMetricsProvider.cs:11` 的"本仓不变式"**互相矛盾**——CI 只跑 100% 故从未暴露（selftest 在高 DPI 真机假红或该不变式为错，○）；`UiSelftestProbe:136` MinimumSize 未折算；`TrayMenuForm.OnDpiChanged` 改 Size 不重夹位置；`NoticeCard.ShowItem` 复用旧实例悬停态残留；`LauncherApp.cs:438-448` 使 `LauncherLifecycle.cs:141-146` 五行"事务中关窗放行"永不可达（死表项）◇；19 处 xUnit1031（`UpdateCheckerTests` ×15 等）+ 1 处 xUnit2020（`DshUpdatePipelineRealTests.cs:69` `Assert.True(false,…)`）✅；`update-drill.ps1:54` 随机端口 while 重试理论无上限（近零概率）◇；仓库根游离 `package-lock.json`（空 lockfile，未 ignore，疑误跑 npm 残留）✅；`RepoRoot()` helper 在 ≥5 个测试文件各复制一份 ◇；NC 拦截字面合规：5 个无边框窗不覆写 WndProc（现均不触发闪影——唯一加回 `WS_THICKFRAME` 的 DshShellForm 已正确拦），铁律"每个无边框窗必须拦"的前提条件应写进注释 ◇。

## 2. 闸与台账核对结论

- **闸未被动摇**：G1–G13+1b 逐条读过实现，无恒真闸、无削弱迹象、退化自检齐全；workflows 四条线 concurrency 齐全且触发真实（最近成功运行 2026-09-20/21）；分层 filter 唯一真相源在 test.ps1 ✓。
- **但五个闸有结构性盲区**（本轮全部有真违例落入）：G5（类型面只数标量四种）、G6（只数同行空 catch）、G10（字面量 `96f` vs 实际 `96.0`）、G12（把带死锁面的 netstat 手写计入"允许 2 处"）、G13（只拦 `@`/斜杠形态，不拦连字符形态包名）。
- **台账**：#4/#5/#7/#9/#14/#15/#21 现状相符；**需补记 #6（位置过期）、#7（低估）、#13（承诺不实）、#17（名单漏项）**。
- Trait 拼写全仓 31 处逐字正确 ✓；`scripts/*.ps1` 有界性与精确清理抽查通过 ✓。

## 3. 与 2026-08-28 轮的关系

上轮 F1/F2/F4/F9 等修复经核对仍在位（版本比较单实现、增量日志扫描、G13 曾建）。本轮 N4 是上轮 §可红性整改**同形状的事故复发**（注释声称已删、守卫块仍在），说明"删假断言"需要配一条会红的闸（1b 只管 Trait，不管路径守卫）。

## 4. 各分区合规确认（负面结果也记录）

- 状态文件写盘除 N12 外全部 AtomicWrite ✓；日志/锁文件读侧 FileShare.ReadWrite ✓；node/npm 生产路径绝对路径直启 ✓；全仓无 `cmd /c` 包装与 `.cmd` shim 直调（G11 taskkill 启动点唯一属实）✓；`AtomicWrite`/SemVer prerelease/build metadata 实现抽查合规 ✓；`ServiceManager.Start` 长驻豁免超时的设计自洽 ✓；更新流水线失败路径保全顺序正确 ✓；快线 1309 全绿、src 零警告 ✓。

## 5. 修复批次执行状态（用户批准，2026-09-21）

| 批次 | 内容 | 涉及发现 | 状态 |
|---|---|---|---|
| B1 | mutex 持有期（句柄交 Main 的 using 作用域）+ 崩溃钩子提前到 CLI 分发前（TryShowFatalDialog 补 CLI 守卫防无人值守挂死）+ SafeMode 拒绝即中止/异常出口补投 EntryFailed + 两协调器拒绝留痕 + RestartAsync 异常兜底折算 StartFailed | N1/N2/N3/N6 | ✅ 完成；G14 新闸两向验证过（造脏 `using var…new Mutex` → 双断言红并点名违例行）；Headless 用例 +4（SafeMode 异常闭合/拒绝跳过、协调器异常折算/拒绝照做） |
| B2 | 假绿清除：删 `SafeModeE2EOutcomes.cs`（3 条：守卫型+断言自己型+E1008 三份重复之一）与 `SafeModeOutcomes.cs`（2 条幽灵 `DSH_SAFE_MODE`——**生产全仓零引用**，且其中 1 条 Theory 断言的是 F16 已废除的旧匹配规则）；`SafeModeSandboxOutcomes` 重写为真契约（生产 `SafeModeState` 落盘沙盒 + 主环境字节分毫不动的可红守卫）；`DshSandbox` 夹具随之瘦身（全部 helper 零调用方）；快线 filter 补 `&Category!=RealNet` | N4/N7 | ✅ 完成；快线 1313→1298：删 3 文件共 13 条展开用例（假绿/重复/幽灵契约，含 1 条 5 行 Theory）+ RealNet 3 条不再计入快线账；**逐条估算与实测差 1 条，未归因**——对账以 `dotnet test --list-tests` 展开计数为准，下次分层审计先做集合代数 |
| B3 | G10 判据从 `/96f` 扩到 `/96、/96f、/96.0`；Program.cs 主窗尺寸与 WindowGeometry 标题栏两处收进 `ShellLogic.DpiScale`；`LayoutChromeRects` 补 dpi≤0 契约用例 | N5 | ✅ 完成；G10 造脏（`dpi / 96.0` 探针行）→ 红 |
| B4 | netstat 回退原位补齐三必须（双流后台排空+限时+超时杀树）；RunTaskKill 补双流排空；`RunPnpmInstall` 挂 ct + 墙钟兜底改真并行等待 + 超时/取消杀树（**B4 计划修正**：审查时写的"改走 RunCapture"实施中被依赖方向挡住——ShellLogic 不得反向调用 Managers，正解是台账 #6 的搬迁前置条件；本轮按台账口径原位修形状、#6/#7 台账已补记） | N8/N9 | ✅ 完成（G12 收紧 2→1 并造脏验证；真机 pnpm 取消未实测，不得声称） |
| B5 | G6 v2 块级静默 catch 扫描（空体/仅注释/单 `return false|null|0`，`G6-EXEMPT` 显式豁免），基线棘轮 130 只许降；清剿最重八处：VerifyChecksum 不再把网络异常伪装成 E1004、ExtractPortableNode 真因入 E1005、msiexec 启动失败/整流程/UAC 拒绝三处留痕 + 无界 WaitForExit→300s、KillProcess 抛错入 E2005 | N10 | ✅ 完成（闸自身盲区被 GATE-PROBE 抓出一次并修复：`catch { } // 注释` 曾被当多行体起点；余 ~130 处存量按棘轮只减不增分批清） |
| B6 | vbs 处置：对齐 DshDiscovery 三级回退 / 从因果地图除名+停止分发 | N11 | ⏸ 待用户裁决路线 |

### 本轮未修、留档的其余发现
N5(台账引用行号漂移已顺带更正)、N13/N14/N15/N16/N17/N18/N19/N20/N21/N22/N23/N24/N26/N27/N28 与全部 P3：判决与证据在本文 §1–§3，其中 N15/N16/N17/N18/N19/N20/N21 属真机敏感项，修前需按测试铁律配 RealOS/E2E 验证，不与本轮快线批次混跑。CHANGELOG `[Unreleased]` 已到 G7 上限 400/400，本轮修复记录**尚未**写入——压缩他条或按台账 #11 口径申请抬限需用户裁决。
