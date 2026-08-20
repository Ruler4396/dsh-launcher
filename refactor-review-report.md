# dsh-launcher 架构重构审查 + 自动化测试交付报告

> 审查基线：`v0.4.0`（含 `Lifecycle/`、`Managers/`、`LauncherApp` 组合根）
> 验证结果：**单测 321 全绿**（新增 7 个用例）；TestHook E2E 编译通过、协议修复（`leaveOpen`）后待全量重跑

## 1. 架构审查报告（维度一）

### 1.1 重构现状（前置结论）
Manager/状态机重构**已部分落地**（commit `966416a` 起），但 `LauncherApp` **未接入 `Program.Main`**——
实际生产路径仍是 `Program.RunStartupPipelineAsync`（SplashForm 后台流水线）。双实现并存 = Headless 测试锁定的行为
与真实启动存在**语义漂移风险**，这是最大架构问题（ADR-008 迁移未完成）。

### 1.2 状态机严谨性
- 转移表 9 态 × 10 触发，显式映射 + Fail-fast，整体设计正确。
- **[已修复] `RuntimeResult.Failed(code, detail)` 丢弃错误码** → 组合根无法区分 E1003/E1004。
  已补 `ErrorCode/ErrorDetail` 字段（`ManagerInterfaces.cs`）。
- **[已修复] WebView 崩溃无状态机表达** → 新增 `WebViewCrashed` 触发器 + `Running→Running` 自转移。
- **[已修复] `EnsureRuntimeAsync` 抛异常时状态机悬停在 `ResolvingRuntime`** → catch 映射 `RuntimeFailed`。
- **[遗留] `ShuttingDown`/`Failed` 是语义 sink**（无 Reset/重试）；`StartingService` 无"服务拉起失败"触发器
  （E2001 只能走 Fatal）；`InitializingUI` 无失败出口。

### 1.3 DI 与解耦
- 接口隔离良好（5 Manager 接口 + 委托注入），`ServiceManager` 探针委托化使其可 Headless 测试。
- **[遗留] `WindowManager` 仍回调 `Program` 静态方法**（`CreatePopup/ApplyShadow/ResolveDarkMode`，部分已委托化）；
  `WindowManager.Instance` 静态单例 + `WebViewManager` 大量 static 可变状态（`_crashCount/MainWeb/RecoveryNeeded`）
  = 隐式循环依赖（Program↔WindowManager）+ 进程级测试污染风险。

### 1.4 异常边界
- **[遗留] `WebViewManager` 多处 `catch { }` 静默吞异常**（ProcessFailed/下载/弹窗），与"P2-8 不再静默"纪律冲突，
  建议至少 `Logger.Info`。
- `ServiceManager.WaitReadyAsync` 正确区分**取消**（OCE 上抛）与**超时**（return false）；`LauncherApp` 现保证取消冒泡。

### 1.5 契约保留
- `ShellLogic` 纯函数契约有 `ContractTests`（C3/C9/C10）锁定；`ServiceManager` 委托化行为逐位保持。
- **[遗留漂移] `LauncherApp.TargetPort()` 硬编码 3080**，未继承 `DSH_WEB_URL/PORT`；超时 180s 与 Main 的 `WaitServiceReady` 需核对。

## 2. 测试矩阵（维度二/三）

| ID | 场景 | 驱动 | 预期状态转移 | 关键断言 | 测试文件 |
|----|------|------|--------------|----------|----------|
| H1 | Happy Path | RunStartupAsync（Fake 全成功） | …→WaitingForReadiness→InitializingUI→**Running** | 状态轨迹完整 + UIInitialized 触发 | LauncherAppScenarioTests |
| H2 | Runtime Failure **E1004** | FakeRuntime.Failed(E1004) | ResolvingRuntime→**Failed** | Failed + ErrorCode==E1004 保留 | 同上 |
| H3 | Readiness Timeout **E2002** | FakeService.Ready=false + staleCleanup | WaitingForReadiness→**ShuttingDown** | ShuttingDown + staleCleanup(3080) 被调用 | 同上 |
| H4 | WebView2 Crash Recovery | 组合根 Running 后 HandleWebViewCrashed | Running→**Running**（自转移） | 状态保持 + 恰好一次广播 | 同上 |
| H5 | 异常边界 | FakeRuntime 抛异常 | ResolvingRuntime→**Failed** | 状态机不悬停 | 同上 |
| S1 | 崩溃非法触发 | Fire(WebViewCrashed) from Idle | 抛 InvalidOperationException | Fail-fast | LauncherLifecycleTests |
| E1 | TestHook ToggleMaximize | NamedPipe 指令 | 真实窗口最大化 | GetWindowRect ⊆ GetWorkArea（≤2px） | UiTestHookE2ETests |
| E2 | TestHook Shutdown | NamedPipe 指令 | 优雅退出 | 进程 10s 内退出 | UiTestHookE2ETests |

## 3. 交付代码
- `src/DshShell/Win32/UiTestHook.cs`（新增）：NamedPipe TestHook，DSH_TEST_MODE=1 且 --ui-probe 激活，
  命令 `ToggleMaximize`/`GetWindowRect`/`GetWorkArea`/`Shutdown`；生产路径零接触。
- `src/DshShell/LauncherApp.cs`：异常边界 catch、RuntimeFailed 错误码记录、staleCleanup 注入、`StateChanged` 事件、`HandleWebViewCrashed`。
- `src/DshShell/Lifecycle/LauncherLifecycle.cs`：`WebViewCrashed` 触发器。
- `src/DshShell/Managers/ManagerInterfaces.cs`：`RuntimeResult` 错误码字段。
- `src/DshShell/Program.cs`：`RunUiProbe` 接线 TestHook（含 Shutdown → form.Close 优雅退出）。
- 测试：`tests/DshShell.Tests/Managers/LauncherAppScenarioTests.cs`（5 场景）、
  `tests/DshShell.Tests/Lifecycle/LauncherLifecycleTests.cs`（+2）、
  `tests/DshShell.E2E/UiTestHookE2ETests.cs`（+2）。

## 4. CI 集成建议（维度四）
- **Headless 单测**：并入 `build.yml` 的 `scripts/test.ps1`（已是唯一测试入口，含静态断言 + dotnet test）。
- **TestHook E2E**：并入 `e2e-multimon.yml`（windows-latest 交互桌面 + 虚拟副屏）；`DSH_TEST_MODE=1` 环境变量注入。
- **门禁分层**：Headless 单测必须全绿；TestHook E2E 建议 soft 门禁（与 GEO_F11_MODE 相同的分层策略）。
