# AGENTS.md — 对本仓库中所有 AI Agent 的强制指示

> **开始任何任务前，你必须先完整阅读并遵守本文件，以及它指向的架构铁律文档。**

## 🚨 第一优先：架构铁律（不读就动代码 = 违规）

本仓库是 `dsh-launcher`——一个涉及复杂进程编排、Win32 底层交互与 WebView2 渲染的 Windows 桌面启动器。

在修改任何 `.cs` 代码、编写测试、调整构建脚本之前，**你必须先通读**：

- **`docs/00-ARCHITECTURE-GUARDRAILS-MANDATORY.md`** ← 架构与工程维护铁律 v1.0（强制约束，非建议）
- **`docs/TESTING-GUARDRAILS.md`** ← 测试体系铁律（SDET 强制约束，Bug 驱动复现）

这份文档是 **Hard Constraints（硬约束）**，不是可选项。违反其中任一条款都会被 Code Review 驳回或 CI 拦截。它规定了：组合根边界、Manager 依赖方向、状态机单一真相源、进程调用的"三必须"、异常透明性、Win32 NC 消息拦截、WebView2 崩溃隔离、契约测试先行等。

**测试铁律（尤其重要）**：禁止"Mock 幻觉"——涉及进程/文件锁/编码的测试必须补 `Category=RealOS` 真实 OS 交互测试；修复 P0/P1 环境 Bug 后**必须**写 `Regression_<Bug>` 零 Mock 复现测试，否则不予合并（详见 `docs/TESTING-GUARDRAILS.md`）。

## 🔥 快速合规清单（动代码前自检）

| 你将要做什么 | 必须先满足 |
|---|---|
| 改 `Program.cs` | 只允许组合根 + UI 消息泵；业务逻辑下沉到 `Managers/`/`Lifecycle/`/`ShellLogic.cs` |
| 调用外部进程（npm/node/wscript/taskkill） | `cmd.exe /c` 包装 + 重定向 stdout/stderr + 超时 + 超时 `Kill(entireProcessTree)` |
| 读可能被锁的日志文件 | `FileShare.ReadWrite` |
| 写核心状态文件 | `ShellLogic.AtomicWrite`（`.tmp` + `File.Move`），禁止裸 `File.WriteAllText` |
| 调用 npm/node | 用 `RuntimeManager` 解析的绝对路径，禁止盲目依赖 PATH |
| 加 `catch` 块 | 禁止空 `catch {}` / `catch { return false; }`；区分 Warn 降级 vs Error+E9001 |
| 写任何用户可见错误弹窗 | 必须带 `[E####]` 错误码 |
| 修改 `ShellLogic` 纯函数 | **先**补/更新 xUnit 契约测试 |
| 修改生命周期流转 | 在 `LauncherAppScenarioTests` 补 Headless 测试 |
| 修改窗口布局/DPI/无边框 | 用 `--ui-selftest` 或 `UiTestHookE2ETests` 验证 0px 间隙，禁肉眼 |
| 动 `test.ps1` 静态断言 | 严禁注释或削弱（God Object 最后防线） |

## 🧭 项目地图（定位代码）

- `src/DshShell/Program.cs` — 组合根（仅启动 + 消息泵，业务下沉）
- `src/DshShell/Managers/` — `ServiceManager`/`RuntimeManager`/`WebViewManager`（互不引用，经 `LauncherApp` 注入）
- `src/DshShell/Lifecycle/` — `LauncherLifecycle` 状态机（`LifecycleState` 枚举 + `Triggers`）
- `src/DshShell/ShellLogic.cs` — 所有纯逻辑（`static` 纯函数 + 契约测试）
- `src/DshShell/Windows/` — `DshShellForm`/`SplashForm` 等 UI
- `tests/DshShell.Tests/` — 单测 + `ShellLogic` 契约测试 + `LauncherAppScenarioTests`（Headless 状态机）
- `tests/DshShell.E2E/` — 真实 GUI E2E（UIA/FlaUI）
- `scripts/test.ps1` — CI 静态断言守护神（含 shell 一致性断言，禁止削弱）

## ✅ 完成后必须自查

- [ ] 是否引入隐式全局状态（`static bool` 控流程）？→ 必须用状态机
- [ ] 是否吞掉异常？用户能否看到真实失败原因？→ 必须透明
- [ ] 是否破坏 Manager 依赖方向？→ 必须经 `LauncherApp` 注入
- [ ] 是否补了契约测试 / Headless 测试？→ 必须
