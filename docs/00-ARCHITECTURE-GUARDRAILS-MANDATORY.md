# 🛡️ 00-ARCHITECTURE-GUARDRAILS-MANDATORY（强制遵守！请先读我）

> **⚠️ 强制约束文档（Hard Constraints）**：在动任何一行代码之前，**必须**完整阅读本文档。
> 本文件是 `dsh-launcher` 的架构与工程维护铁律（v1.0）。
>
> **任何代码提交（人类开发者或 AI Agent）违反以下任一条款，都必须被 Code Review 驳回或 CI 拦截。**

---

> **前言**：本项目是一个涉及复杂进程编排、Win32 底层交互与 WebView2 渲染的 Windows 桌面启动器。为了防止代码库退化为"面条代码"和"隐式状态机"，特制定本铁律。所有 PR 必须通过本文档的合规性审查。

---

## 🏛️ 第一原则：架构边界与依赖方向 (The First Principle)

1. **组合根神圣不可侵犯**：
   - `Program.cs` **仅且只能**作为组合根（Composition Root）和 UI 消息泵的启动器。
   - **严禁**在 `Program.cs` 中编写任何业务逻辑、状态判断或外部进程调用。所有业务逻辑必须下沉至 `Managers/`、`Lifecycle/` 或 `ShellLogic.cs`。
2. **依赖倒置与防循环**：
   - Manager 之间（如 `ServiceManager`, `RuntimeManager`, `WebViewManager`）**严禁直接实例化或引用对方**。
   - 跨 Manager 通信**必须**通过 `LauncherApp`（组合根）注入接口（`IxxxManager`）或事件委托（`Action`/`EventHandler`）。
   - **严禁** Manager 向上回调 `Program` 的静态方法。UI 层向底层传递指令必须通过状态机触发器（Triggers）。
3. **纯逻辑与 UI 隔离**：
   - 任何不依赖 WinForms 句柄、不依赖系统环境的决策逻辑（如路径计算、版本比对、端口校验），**必须**抽取到 `ShellLogic.cs` 中作为 `static` 纯函数，并配备 100% 覆盖的 xUnit 契约测试。

---

## ⏳ 核心约束一：状态机与生命周期 (Lifecycle & State Machine)

1. **状态机是唯一真相源 (Single Source of Truth)**：
   - 所有的启动、更新、崩溃恢复流程，**必须**通过 `LauncherLifecycle` 状态机进行流转。
   - **严禁**使用全局 `static bool`（如 `_isUpdating`, `_hasStarted`）来控制流程。状态必须显式定义在 `LifecycleState` 枚举中。
2. **Fail-Fast 与非法转移**：
   - 状态机必须对非法的状态转移（如在 `Idle` 状态下触发 `Shutdown`）抛出 `InvalidOperationException`（Fail-Fast），**严禁**静默忽略或强行扭转状态。
3. **异步任务的取消与清理**：
   - 任何跨越状态的 `Task`（如 `npm install`、HTTP 轮询），**必须**接收 `CancellationToken`。
   - 当状态机转移到 `ShuttingDown` 或 `Failed` 时，**必须**确保所有后台 Task 被取消，且关联的外部进程（如 `node`, `cmd`）被强杀（`taskkill /T /F`）。

---

## ⚙️ 核心约束二：进程、网络与外部依赖 (Process & External Dependencies)

1. **外部进程调用的"三必须"**：
   - 调用任何外部进程（`npm`, `node`, `wscript`, `taskkill`），**必须**使用 `cmd.exe /c` 包装（解决 `.cmd` 执行陷阱）。
   - **必须**重定向 `StandardOutput` 和 `StandardError`，并使用异步读取（`ReadToEndAsync`）防止管道死锁。
   - **必须**设置合理的超时（`WaitForExit(timeout)`），超时后**必须**调用 `p.Kill(entireProcessTree: true)` 清理僵尸进程树。
2. **网络与 IO 的防锁死机制**：
   - 读取可能被其他进程（如 `cmd >>`）锁定的日志文件时，**必须**使用 `FileShare.ReadWrite`。
   - 写入核心状态文件（如 `pending-update.json`, `window-state.json`），**必须**使用 `ShellLogic.AtomicWrite`（写 `.tmp` 后 `File.Move`），**严禁**直接 `File.WriteAllText`。
3. **环境解析的绝对路径化**：
   - 调用 `npm` 或 `node` 时，**严禁**盲目依赖系统 `PATH`。**必须**优先使用 `RuntimeManager` 解析出的绝对路径（如 `Path.Combine(PortableNodeDir, "npm.cmd")`）。

---

## 🚨 核心约束三：异常处理与可观测性 (Exceptions & Observability)

1. **拒绝"静默吞异常"**：
   - **严禁**出现空的 `catch { }` 或 `catch { return false; }`。
   - 所有 `catch` 块**必须**区分"预期内的操作失败"（记录 `Logger.Warn` 并触发状态机降级）和"编程不变式破坏"（记录 `Logger.Error` 并触发 `E9001` 崩溃留痕）。
2. **日志系统的 Fallback 机制**：
   - 如果主日志文件（`dsh.log`）被独占锁定导致写入失败，`Logger` **必须**自动 Fallback 到 `%TEMP%\dsh-launcher-fallback-{pid}.log`，并向控制台输出警告，**严禁**丢弃关键错误堆栈。
3. **错误码的契约化**：
   - 任何用户可见的错误弹窗，**必须**携带 `[E####]` 错误码。
   - 新增错误场景时，**必须**先在 `ErrorCodes.cs` 中注册，并补充对应的 `Describe` 描述。

---

## 🖼️ 核心约束四：UI、Win32 与 WebView2 纪律 (UI & Win32 Discipline)

1. **非客户区 (NC) 消息的绝对拦截**：
   - 由于去除了 `WS_CAPTION`，`WndProc` 中**必须**拦截并吞掉 `WM_NCACTIVATE` (返回 1) 和 `WM_NCPAINT` (返回 0)，**严禁**将其放行给 `DefWindowProc`（会导致 Win98 经典标题栏闪影）。
2. **DWM 重绘的节流控制**：
   - `ForceNonClientRedraw()`（调用 `SWP_FRAMECHANGED`）**严禁**在 `OnResize` 的高频拖拽周期中无条件调用。**必须**通过 `_lastWindowState` 状态机进行去重，仅在窗口状态（最大化/还原）发生实质变化时调用。
3. **WebView2 崩溃的隔离与恢复**：
   - 插件弹窗（非主窗）的 WebView2 崩溃，**严禁**污染主窗的 `_webviewRecoveryNeeded` 标志和 `_crashCount` 节流器。必须通过 `ReferenceEquals(web, _mainWeb)` 进行严格隔离。

---

## 🧪 核心约束五：测试与 CI 门禁 (Testing & CI Gates)

1. **契约测试先行 (Contract First)**：
   - 任何对 `ShellLogic` 纯函数的修改，**必须**先更新或新增 xUnit 契约测试。CI 中契约测试失败，**直接阻断**构建。
2. **Headless 状态机测试**：
   - 涉及生命周期流转的修改，**必须**在 `LauncherAppScenarioTests` 中补充 Headless 测试（通过 Mock Manager 驱动状态机），验证状态转移和副作用（如清理回调）是否正确触发。
3. **UI 几何与多显示器回归**：
   - 涉及窗口布局、DPI 缩放、无边框渲染的修改，**必须**通过 `--ui-selftest` 或 `UiTestHookE2ETests` 验证"最大化窗口矩形 == 工作区"（0px 间隙）。**严禁**依赖人工肉眼验证。
4. **静态断言守护神**：
   - `test.ps1` 中的静态正则断言（如"Program.cs 中不得包含 Form 子类"）**严禁**被注释或削弱。这是防止 God Object 复活的最后防线。

---

## 📝 变更管理流程 (Change Management Process)

当引入新功能或修复 Bug 时，**必须**遵循以下 Checklist：

- [ ] **架构审查**：我的修改是否破坏了 Manager 之间的依赖方向？是否引入了隐式全局状态？
- [ ] **进程安全**：我是否调用了外部进程？是否处理了超时、僵尸树清理和输出重定向？
- [ ] **异常透明**：我是否吞掉了异常？用户能否通过日志或弹窗看到真实的失败原因？
- [ ] **Win32 兼容**：我的 UI 修改是否触发了未拦截的 NC 消息？是否会导致高 DPI 下的布局错乱？
- [ ] **测试闭环**：我是否补充了对应的契约测试或 Headless 场景测试？
