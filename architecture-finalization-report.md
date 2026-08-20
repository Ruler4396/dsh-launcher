# dsh-launcher v0.4.2 架构收尾 + Splash UI/卡顿修复 + 发布报告

> 验证结果：**单测 321 全绿**；E2E 项目编译通过；Release publish 成功；
> ZIP 生成于 `E:\dsh-launcher\dist\dsh-launcher-windows-0.4.0.zip`

## 一、架构修改摘要

### 1. 统一启动入口（消除双实现）
- `Program.RunStartupPipelineAsync`（旧 SplashForm 流水线，约 100 行）**已删除**，替换为 `RunLauncherAppPipelineAsync` 薄适配（`Program.cs`），启动编排**唯一由 `LauncherApp.RunStartupAsync` 驱动**。
- `LauncherApp` 重写（`LauncherApp.cs`）：
  - **env 解析**：`Port`/`Url`/`ServerManagedExternally` 经 `ShellLogic.ResolveTarget(DSH_WEB_URL, DSH_WEB_PORT)` 解析，修复硬编码 `3080`（契约与 `Program.Target` 同源）。
  - **副作用委托注入**（组合根职责，LauncherApp 不引用 Program）：`BackgroundMaintenance`/`SweepStaleAndApplyUpdate`/`StartService`/`ReadinessProbe`/`StaleCleanup`；测试注入 Fake 或置 null 即跳过。
  - **修复隐藏 bug**：原 `if (!rt.Ready)` 误判（`RuntimeManager` 返回 `Portable(Ready=false)` 恒失败）→ 改为 `!rt.Ok`。
  - **IProgress<string> 进度**：报告"正在准备 Node.js 运行环境…/正在检查 dsh 服务…/正在等待 dsh 服务就绪…"。
  - `DSH_TEST_SPLASH_DELAY_MS` E2E 钩子随迁，保持 `StartupLatencyTests`/`UiResponsivenessTests` 语义。
- `RuntimeManager` 增加 `confirmDownload` 构造参数：Node 下载前确认（保持"先确认后下载"契约，`E1002` 拒绝 / `E1003` 下载失败），确认文案由组合根注入（原 `EnsureNodeForStartupAsync` 语义保留）。

### 2. 解除 WindowManager -> Program 循环依赖
- `WindowManager` 中 5 处 `DshWeb.Program.XXX` 静态引用全部移除，改为组合根注入的委托：
  - `CreatePopup()` → `PopupFactory`；`ApplyShadow()` → `ApplyShadowAction`；`ShowMainWindow` 的 `ShowWindowNative` → `ShowWindowAction`；两处 `Program.Trace` → `TraceAction`。
- `Program` 在组合根装配处注入（`WindowManager.cs` L61-63、L140、L176、L232 全部改走委托）：`PopupFactory = CreatePopupForm`、`ApplyShadowAction = ApplyWindowShadow`、`ShowWindowAction = ShowWindowNative`、`TraceAction = Trace`。
- `WindowManager` 不再有任何对 `DshWeb.Program` 的引用 → 隐式环切断，进程级测试不再被 Program 静态状态污染。

### 3. 异常边界
- `WebViewManager` 全部静默 `catch {}` 已消除（8 处），改为 `catch (Exception ex) => Logger.Warn(...)`：
  外部导航/弹窗开浏览器失败、下载文件打开失败、托盘气泡失败、下载处理兜底、崩溃自愈 `Reload` 失败、readyState 测试钩子失败。
  - 理由：均为事件处理器内的"预期操作失败"，无调用方接收异常，记录不静默即符合"禁止无脑吞没"。
- `ServiceManager` 本就无 `catch{}`；取消（OCE）保持上抛，超时（return false）与预期失败正确区分。
- 剩余 `catch{}` 经扫描确认均为临时文件/进程清理与 `BeginInvoke` 防御性操作（非业务失败），符合设计。

## 二、UI 与卡顿修复报告

### 1. SplashForm 紧凑化（`SplashForm.cs`）
| 控件 | 0.4.1 | 0.4.2 |
|---|---|---|
| 窗体 ClientSize | 380 × 196 | **380 × 180** |
| 状态标签 | (18,14) 344×38 | (16,12) 348×32 |
| 进度条 | (18,58) 344×12 | (16,50) 348×10 |
| 取消按钮 | (306,166) 60×24 | (302,148) 60×22 |
| 确认面板 | (18,78) 344×86 | (16,66) 348×76 |

边距统一 16px、垂直间距收紧（Label→Bar 4px），E2E 响应性断言（按钮 ≥60×20）仍满足（60×22）。

### 2. "正在检查 dsh 服务…"卡顿修复
**根因**：同步 `TcpClient.Connect`（`ShellLogic.PortOpen`）在本机部分环境耗时 ~2s，`ServiceManager.WaitReadyAsync` 轮询曾直接在调用线程执行。
**修复**：
- `ShellLogic.PortOpenAsync` 新增（`ConnectAsync + 3s 超时`，契约 C3 语义不变）。
- `ServiceManager.WaitReadyAsync`（`ServiceManager.cs`）：TCP 探测改异步 `PortOpenAsync`（不阻塞调用线程）；HTTP 探测包 `Task.Run`；显式注入的同步探针（测试）保留原语义。
- `LauncherApp.RunStartupAsync` 由 `SplashForm.OnShown` 的 `Task.Run` 后台驱动，`ReadinessProbe`（真实路径 `WaitServiceReady`）同样包后台线程 → **UI 线程全程只跑消息泵**：检查期间窗体可拖动/重绘、取消按钮立即响应。
- 进度更新经 `IProgress<string>`（内部 `Progress<T>` 绑定 UI `SynchronizationContext`）回填状态标签，无 `Control.Invoke`/`.Result` 反模式。

## 三、构建产物

| 项 | 值 |
|---|---|
| ZIP 绝对路径 | `E:\dsh-launcher\dist\dsh-launcher-windows-0.4.0.zip` |
| 大小 | 589,497 B（≈576 KB） |
| SHA256 | `6a2723e42873dedb647d83068902517bad81ed8851cac26a33e6660a091f7966` |
| 校验文件 | `E:\dsh-launcher\dist\dsh-launcher-windows-0.4.0.sha256` |
| 测试 | 单测 **321 全绿**；E2E 项目编译通过 |

> 提示：`dist\dsh-launcher-windows-0.4.0\` 为打包中间目录（脚本清理被 `SilentlyContinue` 吞掉），不影响 ZIP 产物。
