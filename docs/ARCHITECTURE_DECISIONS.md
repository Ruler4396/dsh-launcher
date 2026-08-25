# 架构决策记录（Architecture Decision Records）

> 本文档收录 `dsh-launcher` 开发过程中的关键架构决策，每条记录包含事故背景、根因分析与不变式约束。
> 源码中的历史事故描述已压缩为 `[INVARIANT]` 注释，完整上下文保留于此。

---

## ADR-001: Main() 必须保持同步签名

**状态**: 已生效  
**日期**: v0.3.0 (2024)  
**影响**: `Program.cs` — `Main()` 入口

### 背景

v0.3.0 将 `Main()` 改为 `async Task Main()` 后，安装后首次真实 GUI 启动即报 `E1006`（`RPC_E_CHANGED_MODE` 0x80010106）。0.2.5 的同步 Main 正常工作。

### 根因

.NET 10 对 `async Task Main` 的入口线程**不应用** `[STAThread]`（`GetApartmentState()` 返回 MTA）。WebView2 环境创建（`CoreWebView2Environment.CreateAsync` → native `CreateCoreWebView2EnvironmentWithOptions`）严格要求 STA 线程，MTA 下必抛 `RPC_E_CHANGED_MODE`。

### 决策

回退为同步 `private static void Main()`。启动流程中唯一的 await 用同步等待（`ShowDialog` 跑嵌套消息循环，窗口仍正常显示/可取消）。主窗 `form.Load` 等事件处理器里的 await 处于 `Application.Run` 消息循环内，有 `WindowsFormsSynchronizationContext` 保障续延回到 STA UI 线程。

### 不变式

```
Main must be synchronous. WebView2 environment creation strictly requires STA thread.
.NET 10 does not apply [STAThread] to async Task Main → MTA → RPC_E_CHANGED_MODE.
```

---

## ADR-002: FormClosing 托盘拦截必须先于 WebView2 销毁

**状态**: 已生效  
**日期**: 0.1.10 (血泪教训)  
**影响**: `Program.cs` — `FormClosing` 事件处理器

### 背景

0.1.10 版本中，WebView2 Dispose 发生在托盘拦截判定之前。用户从托盘恢复窗口时，WebView2 控件已被销毁，窗口只剩空白（白屏）。

### 根因

`WebView2` 一旦 Dispose，从托盘唤起时控件已销毁。拦截路径的 `e.Cancel = true` 只阻止了窗口关闭，但 WebView2 已经被回收。

### 决策

`ShouldInterceptCloseToTray` 判定必须**先于**任何销毁操作。拦截路径返回后才执行销毁/退出逻辑。决策已下沉至 `ShellLogic.LifecycleDecisions.ShouldInterceptCloseToTray` 纯函数（矩阵 L1）。以下所有"销毁/退出"路径都必须在 return 之后才执行——**顺序即语义，禁止重排**。

### 不变式

```
Tray intercept decision MUST precede WebView2 disposal.
WebView2 Dispose destroys the control; tray restore then shows blank window.
ShouldInterceptCloseToTray → return → disposal path. ORDER-INVARIANT.
```

---

## ADR-003: DPI 感知必须在所有窗口创建前设置

**状态**: 已生效  
**日期**: issue #2  
**影响**: `Program.cs` — `InitializeProcessEnvironment()`

### 背景

150% 等缩放下 WebView2 内容被 Windows 做位图拉伸，字体和图标模糊。

### 根因

DPI 感知设置（`SetProcessDpiAwarenessContext`）晚于窗口创建。Windows 在 DPI 感知未声明时对 WebView2 内容做位图缩放。

### 决策

在 Main 最开头用 `user32.SetProcessDpiAwarenessContext` 直接设置 Per-Monitor V2（`DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2` = -4）。不依赖 WinForms 的 `Application.SetHighDpiMode`——部分环境下因先前的 `MessageBox` 等窗口创建而失效。

### 不变式

```
DPI awareness must be set BEFORE any window/control creation.
Otherwise Windows bitmap-scales WebView2 content at >100% DPI (blurry).
Use user32 directly: SetHighDpiMode may fail after MessageBox creation.
```

---

## ADR-004: WinForms 全局初始化必须在窗口创建前完成

**状态**: 已生效  
**日期**: 冷启动流程分析  
**影响**: `Program.cs` — `InitializeProcessEnvironment()`

### 背景

冷启动流程会先创建启动状态窗（`SplashForm`，`IWin32Window`）。若 `SetCompatibleTextRenderingDefault` 在窗口创建后调用，会抛 `InvalidOperationException` 导致进程静默崩溃——主窗口不出现，用户只能二次点击（服务已在跑、跳过状态流后才轮到正常调用）。

### 根因

`Application.SetCompatibleTextRenderingDefault(false)` 必须在任何窗口/控件创建之前调用。SplashForm 先于 Main 中的 WinForms 初始化代码执行时，触发该异常。

### 决策

`Application.EnableVisualStyles()` 和 `Application.SetCompatibleTextRenderingDefault(false)` 必须在 Main 最前面，在任何窗口创建之前执行。

### 不变式

```
WinForms global init (EnableVisualStyles + SetCompatibleTextRenderingDefault)
MUST complete before ANY window/control creation. Otherwise InvalidOperationException.
```

---

## ADR-005: 服务就绪判定必须 TCP + HTTP 双重验证

**状态**: 已生效  
**日期**: P1-6 质量治理  
**影响**: `ShellLogic.ServiceReadiness` — `IsHttpReady`, `PortOpen`

### 背景

仅凭 TCP 端口可连判定服务就绪，会导致主窗白屏（历史"要二次点击"根因）。dsh 前端在端口监听后可能还需数十秒才提供 HTTP。

### 根因

TCP connect 成功只说明有进程在监听端口，不代表 HTTP 服务已就绪。dsh 前端的启动分为：监听端口 → 加载插件/初始化 → 提供 HTTP 服务。中间的窗口期 TCP 通但 HTTP 不通。

### 决策

服务就绪 = TCP 端口可连 **且** HTTP 有响应。任何 HTTP 响应码（含 4xx/5xx）都算"有服务在应答"——dsh 前端存在即就绪。网络异常/超时/拒绝连接 → 未就绪。契约测试锁定此语义。

### 不变式

```
Service ready = TCP port open AND HTTP response received (any status code).
TCP-only check causes white screen (frontend not ready yet).
```

---

## ADR-006: PortOpen 必须有硬超时

**状态**: 已生效  
**日期**: v0.4.0 性能修复  
**影响**: `ShellLogic.ServiceReadiness` — `PortOpen`, `PortOpenAsync`

### 背景

实测部分 Windows 环境（杀软/系统 TCP 栈）对回环 connect 存在稳定 ~2s 延迟（无论端口是否监听、是否显式 IPv4 Loopback）。启动流水线多处同步调用 `PortOpen`（NeedsStart/阶段0/Sweep/就绪探测）叠加会拖慢启动数秒。

### 根因

Windows 回环 TCP connect 的延迟受系统 TCP 栈和安全软件影响，非可控因素。

### 决策

给 `PortOpen` 的 `ConnectAsync` 加 300ms 硬超时。本机回环正常连接（ACK）毫秒级、未监听端口超时即视为不可用——300ms 足够判定"服务就绪与否"，超时按 false 处理不误伤。

### 不变式

```
PortOpen connect must have 300ms hard timeout.
Loopback connect is millisecond-level; timeout = port not listening.
System-level ~2s delay on some environments must not block startup pipeline.
```

---

## ADR-007: WebView2 数据目录必须单实例占用

**状态**: 已生效  
**日期**: 质量治理（实测事故）  
**影响**: `Program.cs` — WebView2 初始化

### 背景

多个 `dsh-launcher` 实例共用同一 WebView2 user-data-dir（`%LOCALAPPDATA%\DshWeb\WebView2`）会导致互相锁死：真实实例 UI 线程卡死、整窗灰色无响应。native 返回 `0x800700B7`（`ERROR_ALREADY_EXISTS`）。

### 根因

WebView2 的 user-data-dir 是排他锁定的。多实例共用时，第二个实例的 `CreateAsync` 会等待锁（无限期），导致 UI 线程卡死。

### 决策

检测到 `0x800700B7` 时给出专属提示（"另一个 dsh-launcher 实例正在占用数据目录"），引导用户关闭其他窗口。测试钩子 `DSH_WEBVIEW2_DATA` 允许自动化测试隔离数据目录。

### 不变式

```
WebView2 user-data-dir is exclusively locked. Multi-instance sharing causes UI deadlock.
Detect 0x800700B7 and prompt user; tests must use DSH_WEBVIEW2_DATA for isolation.
```

---

## ADR-008: IME 管理必须由 WebView2 内部处理

**状态**: 已生效  
**日期**: 0xc0000005 崩溃修复  
**影响**: `Program.cs` — WebView2 控件创建

### 背景

WinForms 对 WebView2 宿主控件的 IME 状态管理会调用 `ImmSetOpenStatus`。在 WebView2 抢占输入法上下文时偶发无效 `HIMC` → 访问违规崩溃（0xc0000005）。用户 20:53 更新后主窗崩溃。

### 根因

WebView2 内部自带 IME 处理（Chromium 内核），不需要 WinForms 的 `ImeMode` 介入。WinForms 的 IME 管理与 WebView2 的 IME 处理冲突。

### 决策

将 WebView2 控件的 `ImeMode` 设为 `ImeMode.Disable`，让 WinForms 完全跳过 IME 管理。

### 不变式

```
WebView2 ImeMode must be Disable. WinForms IME management conflicts with Chromium's.
ImmSetOpenStatus with invalid HIMC → 0xc0000005 crash.
```

---

## ADR-009: NC 消息必须拦截（无边框窗口）

**状态**: 已生效  
**日期**: ADR-001 无边框标题栏  
**影响**: `DshShellForm.cs` — `WndProc`

### 背景

去除 `WS_CAPTION` 后，`DefWindowProc` 处理 `WM_NCACTIVATE` 和 `WM_NCPAINT` 会导致 Win98 经典标题栏闪影。

### 根因

无边框窗口（`FormBorderStyle.None`）移除了 `WS_CAPTION`，但 `DefWindowProc` 仍会尝试绘制非客户区。

### 决策

`WndProc` 中拦截并吞掉 `WM_NCACTIVATE`（返回 1）和 `WM_NCPAINT`（返回 0），严禁放行给 `DefWindowProc`。

### 不变式

```
WndProc MUST intercept WM_NCACTIVATE (return 1) and WM_NCPAINT (return 0).
DefWindowProc on borderless window → Win98 classic title bar flash.
```

---

## ADR-010: 日志文件必须用共享模式读取

**状态**: 已生效  
**日期**: P1-1 质量治理  
**影响**: `ShellLogic.ServiceReadiness` — `ReadLinesShared`, `ReadLogTail`

### 背景

运行中的 dsh 服务以 `cmd >>` 重定向持有 `dsh.log`（独占写共享）。默认 `FileShare.Read` 无法再开写句柄，曾导致 `--diagnose` 和日志读取失败（历史"日志静默丢失"根因）。

### 根因

`cmd.exe` 的 `>>` 重定向以 `FileShare.Read | FileShare.Write` 模式打开文件。默认的 `FileShare.Read` 要求独占读，与写共享冲突。

### 决策

读取日志文件时必须使用 `FileShare.ReadWrite` 共享模式。统一实现 `ReadLinesShared` 供 `ReadLogTail` 和 `DiagnoseExport` 共用。

### 不变式

```
Log file reads MUST use FileShare.ReadWrite.
cmd >> redirect holds exclusive write share; FileShare.Read fails with sharing violation.
```

---

## ADR-011: PID 身份校验防复用误杀

**状态**: 已生效  
**日期**: P1-2 质量治理  
**影响**: `ShellLogic.ProcessManagement` — `IsLikelyDshService`

### 背景

PID 文件里的 PID 可能被系统复用给无关进程。杀进程前必须确认该 PID 确为 dsh 服务（node 进程），否则误杀用户进程。

### 根因

Windows PID 是有限资源，进程退出后 PID 会被系统回收并分配给新进程。

### 决策

杀进程前必须：① 确认进程名为 `node`（`IsLikelyDshService`）；② 确认进程正在监听目标端口（`FindPidListeningOn`）。双重校验任一失败则拒绝 kill。

### 不变式

```
Before killing: verify process name is "node" AND it listens on target port.
PID reuse is real; dual verification prevents killing innocent processes.
```

---

## ADR-012: 端口探测必须用 P/Invoke（GetExtendedTcpTable）

**状态**: 已生效  
**日期**: 任务一（精确端口归属）  
**影响**: `ShellLogic.ProcessManagement` — `GetProcessIdByPort`

### 背景

仅靠 `netstat -ano` 字符串解析定位端口进程，存在解析不可靠、外部进程启动开销等问题。

### 根因

`netstat` 输出格式在不同 Windows 版本/语言下可能有差异，字符串解析存在脆弱性。

### 决策

优先用 `GetExtendedTcpTable`（iphlpapi.dll）P/Invoke 精确反查端口→监听 PID（亚毫秒、无外部进程）。失败/无结果时回退 `netstat -ano` 解析兼容异常环境。

### 不变式

```
Port→PID lookup: prefer GetExtendedTcpTable (P/Invoke, sub-ms).
Fallback to netstat -ano parsing for compatibility.
```

---

## ADR-013: 进程树清理必须包含父外壳

**状态**: 已生效  
**日期**: 任务一（僵尸树清理）  
**影响**: `ShellLogic.ProcessManagement` — `KillProcessTree`, `GetAncestorPids`

### 背景

`taskkill /T /F` 只向下杀子进程，不会结束父外壳（cmd/npx）。dsh 服务由 `wscript → cmd → node` 链路拉起，`taskkill /T` 杀 node 后 cmd 外壳残留。

### 根因

`taskkill /T` 的"Tree"是向下遍历子进程，不向上追溯父进程。

### 决策

用 `CreateToolhelp32Snapshot` 构建进程 PID→父 PID 快照，向上收集祖先进程链（最多 8 层）。清理时先杀目标 PID，再杀其祖先链中的 cmd/npx 外壳。

### 不变式

```
taskkill /T only kills children, not parent shells.
Must collect ancestor PIDs (CreateToolhelp32Snapshot) and kill parent cmd/npx shells.
```

---

## ADR-014: npm 执行必须绕过 cmd.exe 包装

**状态**: 已生效  
**日期**: v0.4.0 重写  
**影响**: `Program.cs` — `RunNpmCommand`

### 背景

`npm.cmd` 通过 `cmd.exe /c` 执行存在两类陷阱：① `.cmd` 文件的 GBK/UTF-8 编码冲突（`chcp 65001` 无效）；② `cmd /c` 的引号剥离导致 `ERROR_INVALID_NAME`（用户 22:2x 下载 E4001 根因）。

### 根因

`npm.cmd` 是批处理文件，`cmd.exe` 解析时涉及代码页转换和引号处理。Windows 中文系统的默认代码页（GBK）与 npm 输出的 UTF-8 冲突。

### 决策

彻底抛弃 `npm.cmd/npm.bat` 和 `cmd.exe /c` 包装。直接用 `node.exe` 执行 `npm-cli.js`（探测 `npm-cli.js` 绝对路径）。`node.exe` 输出统一 UTF-8，双编码显式设置保证任何代码页可读。

### 不变式

```
Execute npm via node.exe + npm-cli.js directly. Never via npm.cmd/cmd.exe.
npm.cmd has GBK/UTF-8 encoding conflicts and cmd /c quote stripping issues.
```

---

## ADR-015: 更新承诺必须诚实（不承诺做不到的事）

**状态**: 已生效  
**日期**: v0.4.0 Bug 修复  
**影响**: `Program.cs` — `ApplyPendingDshUpdate`, `DownloadDshUpdateStaged`

### 背景

更新文案硬编码"预计 5-10 秒"，但当依赖预热失败时，npm 现场下载 530 包需 450s+。用户实测后发现承诺与实际严重不符。

### 根因

文案基于"最佳情况"（缓存预热成功）编写，未考虑预热失败的回退路径。

### 决策

文案必须基于**真实状态**：
- 本地 tarball + 预热成功 → "依赖已就绪"（可承诺秒级）
- 本地 tarball + 预热失败 → "正在解析依赖，可能需要几分钟"
- 无 tarball → "需要在线下载，可能需要几分钟"

严禁写死"预计 5-10 秒"这类做不到的保证。

### 不变式

```
Update progress text MUST reflect actual state (tarball + prefetch status).
Never hardcode time promises that cannot be guaranteed.
```

---

## ADR-016: 窗口位置记忆必须用 96dpi 逻辑值

**状态**: 已生效  
**日期**: v0.3.0 + v0.3.1 修复  
**影响**: `Program.cs` — `SaveWindowState`

### 背景

v0.3.1 修复：保存的是含边框的窗口尺寸（`SaveWindowState` 用 `Bounds`），必须赋给 `Size` 而非 `ClientSize`，否则窗口会比保存时大一圈（边框差值）。此前用 `RestoreBounds` 导致位置记忆从未生效（Normal 时恒为初始字段值 (-1,-1,初始尺寸)）。

### 根因

WinForms 的 `RestoreBounds` 只在窗口最小化/最大化时更新，Normal 状态下恒为初始值。

### 决策

位置与尺寸存 96dpi 逻辑值（跨 DPI 恢复时按当前 DPI 缩放）。Normal 状态用 `form.Bounds`（当前真实边界），最小化/最大化用 `form.RestoreBounds`（还原后的边界）。

### 不变式

```
Window state: save 96dpi logical values. Normal → Bounds; Min/Max → RestoreBounds.
RestoreBounds is stale (-1,-1) in Normal state; never use it there.
```

---

## ADR-017: 日志轮转仅在无活服务占用时执行

**状态**: 已生效  
**日期**: v0.4.1 极速启动  
**影响**: `Program.cs` — `RunBackgroundMaintenance`

### 背景

端口探测（`PortOpen` 为同步 TCP connect）在部分环境需 2s。将日志轮转/超长告警所需的端口探测放在 Main 中会阻塞 UI 线程，延迟窗口出现。

### 决策

日志轮转/超长告警已移出 Main，进入 SplashForm 后台流水线阶段 0（`Task.Run`）。轮转前检查端口是否被占用——有活服务时不轮转（避免干扰正在写日志的服务进程）。

### 不变式

```
Log rotation runs in background (Task.Run), not on UI thread.
Only rotate when no live service is using the log file (port not open).
```

---

## ADR-018: 升级场景的旧版本清理必须精确过滤

**状态**: 已生效  
**日期**: 产品安装治理  
**影响**: `ShellLogic.UpgradeProducts` — `FilterByUpgradeCode`, `PickOldInstalls`

### 背景

注册表中可能有其他恰好同名为 "dsh-launcher" 的软件。仅靠 `DisplayName` 匹配会误清理其他产品。

### 决策

用 MSI 的 `UpgradeCode` 精确过滤：只有 UpgradeCode 与本项目固定值 `{3B29D055-E142-43BD-ADA8-C5377D11BD7E}` 一致的产品才算 dsh-launcher。`UpgradeCode` 读取失败或不匹配时宁可不清理。

### 不变式

```
Old version cleanup: verify UpgradeCode matches project constant.
Read failure → exclude (conservative: never delete unknown products).
```

---

## ADR-019: dsh 运行时身份必须统一（DshRuntimeIdentity）

**状态**: 已生效  
**日期**: 2024-Q4 测试幻觉治理  
**影响**: `UpdateChecker.cs`, `Program.cs`, `Domain/DshDiscovery.cs`, `Domain/DshRuntimeIdentity.cs`

### 背景

2024-Q4 深度架构审查发现"测试幻觉"根因：400+ 测试全绿，但真实环境中"自动更新"等核心功能频繁失败。

**身份错位（Identity Mismatch）**：
- `UpdateChecker.ResolveLocalDshVersion()` 执行 `cmd.exe /c dsh —version`——仅检测全局 npm 安装
- `start-dsh.vbs` 使用三级回退链：`where dsh` → `%APPDATA%\npm\dsh.cmd` → `npx -y @deepseek-ai/dsh`
- `Program.ReadGlobalDshVersion()` 执行 `npm root -g` 读取 package.json

三个模块各自独立探测 dsh 的位置和版本，使用完全不同的机制。

### 根因

"检查的 dsh"和"启动的 dsh"不是同一个抽象对象：
- 用户通过 npx 缓存运行 dsh 0.1.0-rc.6
- UpdateChecker 执行 `dsh —version` 找不到全局安装 → 返回 null → 认为"无本地版本"
- 永远检测不到更新 → "更新了全局 npm 包，但实际运行的是 npx 缓存"

### 决策

引入 `DshRuntimeIdentity` 核心领域对象，统一"发现、启动、检查、更新"的身份：
- `DshDiscovery.DiscoverCurrentRuntime()` 是系统中唯一合法的"dsh 在哪里"探查点
- 与 `start-dsh.vbs` 的三级回退链保持一致
- `UpdateChecker.ResolveLocalDshVersion()` 和 `Program.ReadGlobalDshVersion()` 委托 `DshDiscovery`
- 严禁各模块自行 blind-guess（盲猜）运行环境

### 不变式

```
All dsh version/start decisions MUST use DshDiscovery.DiscoverCurrentRuntime().
No module may independently probe dsh location or version.
DshRuntimeIdentity.Source must match the actual invocation path.
```

---

## ADR-020: Outcome Contract 测试替代细碎单元测试

**状态**: 已生效  
**日期**: 2024-Q4 测试幻觉治理  
**影响**: `tests/DshShell.Tests/Outcomes/`

### 背景

现有 400+ 测试大量验证内部函数（URL 编码、版本字符串比较、端口探测），却没有验证"用户任务级不变量"：
- 测试全绿，但"更新了但没生效"的幽灵 Bug 仍存在
- 测试过拟合实现细节，重构时大量测试需要重写

### 决策

建立 5 条顶级的"业务完成态契约（Outcome Contracts）"：
1. `Update_Changes_Actual_Running_Version` — 更新后实际运行版本是否改变
2. `Update_Failure_Retains_Old_Runtime` — 更新失败后旧环境是否完整保留
3. `ForeignPort_DetectedAsConflict_NotKilled` — 孤儿服务是否被正确识别
4. `WebViewCrash_DoesNotCrashLauncher` — WebView 崩溃后状态机是否正确转移
5. `ConfigDegradation_PluginMissing_FallsBackToDefault` — 配置降级是否回退安全默认值

这些测试不关心内部调用了哪个函数，只关心系统的最终物理状态。

### 不变式

```
Outcome tests verify final physical state, not internal function calls.
Every cross-module bug must have a corresponding Outcome Contract.
```

---

## ADR-021: 严禁使用 cmd.exe 包装 Node.js 脚本

**状态**: 已生效  
**日期**: 2024-Q4  
**影响**: `Program.cs`, `DshDiscovery.cs`, `JsEntryResolver.cs`

### 背景

项目长期依赖 `cmd.exe /c` 包装 `.cmd`/`.bat` shim 来执行 npm/pnpm/dsh，导致：
- 中文乱码（cmd.exe 默认 GBK 编码）
- 路径空格转义失败（`ERROR_INVALID_NAME`）
- 进程 Kill 不干净（cmd.exe 中间层）
- GUI 进程 PATH 继承失效

### 根因

`npm`, `pnpm`, `dsh` 本质上都是 JavaScript 文件。Windows 生成的 `.cmd` shim 只是 `node.exe <entry.js>` 的薄包装。直接使用 `node.exe` 执行 `.js` 入口可以彻底绕过所有 `.cmd` 相关陷阱。

### 决策

建立 `JsEntryResolver` 静态类，统一解析 npm/pnpm/dsh 的 JS 入口路径：
- `ResolveNpmCliJs(nodeExePath)` → `npm-cli.js`
- `ResolvePnpmEntry()` → `pnpm.cjs`
- `ResolvePackageEntry(packageName)` → 读取 package.json 的 bin 字段

所有外部命令调用统一使用 `node.exe "<entry.js>" args` 格式，**严禁**出现 `cmd.exe /c`。

### 不变式

```
All Node.js script execution MUST use node.exe + .js entry directly.
cmd.exe /c is PROHIBITED in src/ .cs files (enforced by CI static assertion).
```

---

## ADR-022: 安全模式采用"隔离空 profile + --profile 指向"

**状态**: 已生效  
**日期**: 2024-Q4 安全模式重构  
**影响**: `SafeProfileBuilder`（新）、`ServiceManager`/`Program.cs`（服务 cmdline）、`DshRuntimeIdentity`、`WebViewManager`（崩溃签名捕获）、`Windows/DshShellForm`（横幅）

### 背景

原"安全模式"通过 `DSH_SAFE_MODE=1` → `start-dsh.vbs` 加 `--safe-mode` 参数实现。**实测验证：当前 rc7 真实 dsh 根本没有 `--safe-mode` 选项**（`dsh web --help` 仅 `--host/--port/--trusted-host`），`dsh web --port X --safe-mode` 报 `unknown option '--safe-mode'`。因此安全模式从未对真实 dsh 生效：点"是"后 dsh 因 unknown option 启动失败、服务不起来、窗口消失。

### 根因

壳把"禁用插件"幻想成 dsh 提供 `--safe-mode` 开关，但 dsh 从未实现它。需要一个**壳自主实现**、不依赖 dsh 内部机制、且**不改动用户文件**（`~/.dsh/profiles/web`）的屏蔽插件方案。

### 备选方案与否决

| 方案 | 结论 |
|---|---|
| **node `--import` preload 拦截 bundle 读取** | **否决**。拦截面（bundle 读取路径/函数/格式）恰是 dsh 破坏性更新的高发面，且失败模式为静默假阳性（改了内部函数名不报错但拦截失效）。仅作为未来后备策略的预留缝（`ISafeModeStrategy` 接口），本次不实现。 |
| **`--patch` overlay 禁用所有 bundle** | **否决**。overlay 只能追加不能移除 profile 自带的 `dsh.profile.bundles`。 |
| **隔离空 profile + `--profile` 指向** | **采纳**。`--profile` 是公开 CLI 契约，失败模式响亮可观测（进程非零退出/报错），天然满足"安全模式自身失败要脸红失败"诉求。 |

### 决策

安全模式采用**隔离空 profile + `--profile` 指向**：

1. **位置**：`--profile` 实测**只接受 name 不接受 path**（含 `/` 或 `\` 报 `invalid profile name`），因此隔离 profile 落盘于 `$DSH_HOME/profiles/.dsh-safe`（与用户 `profiles/web` 隔离；`~/.dsh` 由 DSH_HOME 指向，测试时隔离）。
2. **SafeProfileBuilder**（幂等，每次安全启动前重建）：
   - 浅拷贝用户 `profiles/web` 的 manifest 类文件（`package.json` 等配置），**不拷贝 node_modules**；
   - strip bundle：**保留 dsh 自带核心**（`@deepseek-ai/dsh-base`、`@deepseek-ai/dsh-web-app`，提供 web UI），**剥离第三方/用户插件**（`dsh-notification`、`@liustack/modlens`、`dsh-launcher-lifetime` 等，崩溃源）；compat 已知键 `dsh.profile.bundles`、`dependencies` 中的插件项；best-effort。
   - 用户原文件**只读不写**。
3. **cmdline**：安全模式启动 `dsh --profile .dsh-safe web ...`（根级 `--profile`，非 `web` 子命令，避 `rejectParentOptions`）。实测空 profile（含核心）可启动 web 且 HTTP 200、`__DSH_BOOT__` 存在。
4. **Identity 收敛**：`DshRuntimeIdentity` 增加 `Profile` 字段（`Normal | Safe(ProfilePath)`），安全模式启动的 cmdline 必须含 `--profile .dsh-safe`。

### 证据链（实测）

- `dsh --profile <path> ...` → `invalid profile name`（只收 name）。
- `dsh --profile .dsh-safe web --port X`（根级）→ 核心 bundle 剥离第三方后成功监听、`dsh web: http://127.0.0.1:X`、HTTP 200、含 `__DSH_BOOT__`。

### 不变式

```
Safe mode = isolated .dsh-safe profile + --profile pointer (public CLI, loud failure).
Profiles built by SafeProfileBuilder keep ONLY @deepseek-ai core bundles; user/3rd-party
plugins stripped. User profiles/web files are READ-ONLY, never mutated.
--profile accepts name only; safe profile dir = $DSH_HOME/profiles/.dsh-safe.
node --import preload intercept is REJECTED (silent-false-positive risk) — ISafeModeStrategy reserved.
```

---

## ADR-023: 启动崩溃检测采用"多源主动拉取融合"（壳坐四个观察位，不依赖 dsh 主动上报）

**状态**: 已生效  
**日期**: 2026-08 boot 健康监控  
**影响**: `ShellLogic.BootGuard`（纯函数）、`Lifecycle/BootHealthMonitor`（新）、`WebViewManager`（探针执行器/CDP 订阅）、`SafeModeState`（lastFailure 证据）、`DiagnoseExport.ExportTo`、`ErrorCodes`（E2007/E2008）、`Program.cs`（组合根接线）

### 背景

客户端 boot 失败（如 `__ModuleLoader__` facade 缺失、插件加载崩溃、服务启动后即死）此前只有两条被动通道：dsh 的 `plugin-crash` postMessage 与 WebView2 渲染进程崩溃事件。两者都依赖"dsh 活着且愿意上报"——服务进程直接死亡、页面 JS 全局异常、日志打印插件错误时，壳一无所知，用户只看到白屏。

### 决策

壳**主动拉取**四层观察信号 + 一层精确采集，由 `BootHealthMonitor` 三态状态机（Pending/Healthy/Failed）融合判定：

| 层 | 触发语义 | 错误码 |
|---|---|---|
| Process | RecordServicePid 后 attach `Process.Exited`；非零退出/消失 → failed（附真实 exit code）；exit 0 忽略（壳主动停止防线） | E2007 |
| Log | 统一日志**字节偏移增量**扫描签名表（专属签名前置：`plugin load failed` 等 + `DSH_BOOT_SIGNATURES.log_error_signatures` 追加）；命中 → failed（附命中行原文）。壳自写条目（JSON Lines 带 `"code":"E####"` 契约字段）不参与判定 | E2003 |
| HTTP | ready 后轮询 `Target.Url`；连续 ≥2 次 miss 才判死（单次抖动容错） | E2004 |
| Page（主触发器） | NavigationCompleted 起：grace 后按间隔 `ExecuteScriptAsync` 探针。坏签名一次 → failed；好符号 → healthy 停止探针；连续 absent_threshold 次缺席 → failed。**坏签名优先于好符号**（boot 标志先设、插件后崩，好符号不得遮蔽崩溃） | E2008 |
| CDP | `Runtime.exceptionThrown` 只采集原文入证据，**无判定权** | — |

关键机制：
1. **BootSignature 单点配置**（`ShellLogic.BootGuard.BootProfile`）：good_symbol/bad_signatures/grace_ms/probe_interval_ms/absent_threshold 集中定义，可被 `DSH_BOOT_SIGNATURES`（JSON）逐字段覆盖（沙盒注入假签名的验收通道）；配置损坏回退全默认。
2. **Failed 吸收态**：终态后其他层继续补证据（S24：进程层先死、HTTP 层随后补充），`VerdictUpdated` 事件驱动 safe-mode-state.json 重写，融合视图最终完整。
3. **判定前证据缓冲**：CDP 异常常先于判定到达（页面加载即抛错），先进 `_earlyEvidence` 缓冲，失败裁决创建时并入——证据不丢。
4. **每会话一次询问**：`TryConsumeSessionPrompt` 闸门统一 PluginCrashDetected（postMessage 路径）与 BootHealthMonitor.Failed 两条入口；答"否"本会话不再弹。安全模式复用 ADR-022 两级阶梯（L1 KeepDeepSeekCore → L2 Minimal），重启窗口 `Suspend()/ResumeAfterRestart()` 屏蔽全部判定。
5. **误报防护铁律**：探针自身异常只 Warn 绝不判 failed；无效结果不计缺席；慢启动由 grace+absent_threshold 预算覆盖（S23 实测 8s 迟到好符号零误报）。

### 实测教训（每条都已固化为契约测试或 [INVARIANT] 注释）

| 教训 | 根因 | 固化 |
|---|---|---|
| 日志层误判壳自写行 | 壳的 E1008 行含 "bootstrap facade is missing" 文本 | `IsShellAuthoredLogEntry`（E#### 契约过滤） |
| 页面探针静默挂死 | UI 线程上 `GetResult()` 等待需 UI 线程投递完成的 Task = 自我死锁 | 执行器必须 `await`（async void + tcs 兜底） |
| 探针结果恒 Invalid | ExecuteScriptAsync 对脚本返回值再编码一层字符串字面量 | 求值器解一层双重编码 |
| 好符号遮蔽崩溃 | `__DSH_BOOT__` 先设置、插件后崩 | 坏签名优先于好符号 |
| Healthy 后进程死亡无感知 | （设计期即规避）Healthy 只停页面探针，进程/HTTP 层持续值守 | S24 场景验收 |

### 不变式

```
Boot failure detection is PULL-based: the shell observes process/log/http/page itself;
it never relies on dsh reporting its own death.
Page probe: bad signature beats good symbol; probe exceptions NEVER judge failed.
Log layer judges SERVICE output only — shell-authored JSON lines (code:"E####") are excluded.
HTTP layer needs 2 consecutive misses; exit code 0 is ignored (intentional-stop guard).
Healthy stops ONLY the page probe; process/http layers keep watching (post-ready regression).
Evidence fusion view is re-persisted on post-failure appends (VerdictUpdated).
Safe-mode ask happens at most once per session, shared by all detection paths.
```

---

## ADR-024: 系统模型升级——确立 DshRuntimeIdentity 为唯一真相源，消灭双轨制

**状态**: 已生效  
**日期**: 2026-09 系统模型升级  
**影响**: `Domain/DshRuntimeIdentity`（重定义）、`Managers/ManagerInterfaces`（契约重写）、`Managers/RuntimeManager`、`Managers/ServiceManager`（`Start(DshRuntimeIdentity, ...)`）、`Managers/DshUpdateManager` + `IDshUpdateManager`（更新引擎唯一入口）、`Managers/ProcessRunner`（进程/npm 原语）、`Managers/ServiceLifecycleOps`（PID 账本/停服）、`Managers/AppEnvironment`、`Managers/WebRuntimeInstaller`、`Managers/SelftestReporter`、`Windows/LegacyUpgradeCleanup`、`Program.cs`（组合根瘦身 ~4000→~2800 行）、`scripts/test.ps1`（双轨制门禁）、`tests/DshShell.Tests/Outcomes/SystemUpgradeOutcomeContracts`（5 条 L3 Outcome）

### 背景

升级前系统存在**双轨制**：同一件事"把 dsh 跑起来"有两条并行实现——
旧轨：`Program.Main → wscript start-dsh.vbs → cmd.exe`，内部再做 where-dsh/npm-shim/npx 三级回退；
新轨：`RuntimeManager → ServiceManager`。
两轨各自探测版本与路径，"检测到的 dsh"与"启动的 dsh"可以不是同一个（FP1 身份错位事故的直接根因）。同时 `Program.cs` 仍是事实上的 God Object：HTTP 客户端、npm 执行、注册表、msiexec、PID 管理散落其间，Manager 的依赖方向被架空。

### 决策

1. **Identity 即真相源**：`DshRuntimeIdentity(Source, NodeExePath, DshEntryJsPath, Version, ProfilePath?)` 是全系统唯一合法的"dsh 在哪、什么版本"载体。
   - `IRuntimeManager` 只产出 Identity（`Task<RuntimeResolution>`）；
   - `IServiceManager.Start` **必须**接收 Identity，启动命令只能由 `Identity.NodeExePath × Identity.DshEntryJsPath` 拼装（`ShellLogic.ServiceLaunch.BuildArgs`），wscript/cmd.exe 中间层整体废除；
   - `IUpdateManager` 全部接口 Identity 化；跨 Manager 严禁裸传版本字符串/包名/相对路径。
   - 物理要件可空是刻意的：External/NpxCache 形态没有本地 node/入口——此时 `CanLaunchDirectly=false`，启动层响亮 E2001，绝不静默落 npx 冷路径（首装链负责先补装再重发现）。
2. **组合根纯净度铁律**：`Program.Main` 退化为纯装配（环境初始化 + Manager 接线 + 消息泵）。业务原语全部迁出：
   - 进程/npm → `ProcessRunner`；PID 账本/停服/接管 → `ServiceLifecycleOps`；环境探查/迁移 → `AppEnvironment`；
   - WebView2 安装/外链打开 → `WebRuntimeInstaller`；旧版卸载 UI 流程 → `Windows/LegacyUpgradeCleanup`；
   - 自测落盘 → `SelftestReporter`。`test.ps1` 新增 **2.2 双轨制门禁**：`new HttpClient / Process.Start / cmd.exe / wscript / taskkill / msiexec / File.Delete / Directory.Delete / Registry. / 裸 PortOpen / TcpClient / File.WriteAllText` 在 Program.cs 实际代码行中出现任意一个 → CI 标红。
3. **五条 L3 Outcome 契约**（`tests/DshShell.Tests/Outcomes/SystemUpgradeOutcomeContracts.cs`，零 Mock 外部进程，只认物理终态）：更新改变实际运行身份 / 更新失败保留旧运行时 / 安全模式物理隔离 profile / 崩溃 10s 内发重载信号 / 退出零污染（~/.dsh、HKCU Run、npm 全局目录逐项快照比对）。

### 不变式

```
DshRuntimeIdentity is the single source of truth: managers exchange identity,
never raw versions/package names/relative paths.
Service launch command is derived ONLY from Identity.NodeExePath × DshEntryJsPath;
no cmd.exe/wscript intermediary may re-enter the launch chain.
Program.cs (composition root) contains zero business primitives — enforced by test.ps1 gate 2.2.
Update success is judged by re-discovered identity (LogPostApplyIdentity), never by npm exit code.
```

### 与既有 ADR 的关系

- **ADR-021**（禁 cmd.exe 包装）：本 ADR 把该原则从"npm 调用"推广到"整条服务启动链"，并给出静态门禁执行机制。
- **ADR-022**（安全模式隔离 profile）：`--profile` 只收 name 的 dsh 契约由 `BuildArgs` 统一兑现（ProfilePath 携带完整物理目录、命令行只取目录名），物理存在性由 Outcome 3 锁定。
- **ADR-020**（Outcome 契约）：5 条新测试是该方法论在"系统模型升级"上的落地样本。

---

## 附录 A：被拒 / 降级方案档案

> 自 `docs/v0.3.0-plan.md`（已归档删除）第六章归并。记录评审中被拒绝或降级的方案与原因，避免重复论证。

- **主题 accent 增强** → **拒绝**。dsh 只有 dark/light/system 三档、无法读取自定义主题色做动态 accent，保持现状不动。
- **"镜像延迟测速选最快"** → **拒绝**。改为**静态回退链**（自定义 → 上次成功 → nodejs.org → npmmirror），不做网络测速的复杂度。
- **"运行时静默装 .NET"** → **拒绝（壳内）**。技术不可能：壳本身需要 .NET 才能运行，无法自我引导。改由 **MSI 链路 winget 引导**（v0.3.1 已实现）。
- **SIGINT 优雅终止** → **原降级为 P2 尽力而为**；v0.3.1 已实现。
- **自制下载管线** → **拒绝**。npm 当下载器（`npm pack` / `npx`），不自建带仓库逻辑的下载管线。

---

*本文档随架构决策变更持续更新。每条 ADR 的源码位置以 `[INVARIANT]` 注释标注。*
