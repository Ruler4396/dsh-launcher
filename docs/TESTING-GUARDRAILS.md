# 🛡️ TESTING-GUARDRAILS（测试体系铁律，SDET 强制约束）

> **⚠️ 与 `00-ARCHITECTURE-GUARDRAILS-MANDATORY.md` 同级，均为强制约束（Hard Constraints）。**
> 在新增/修改任何测试或 CI 配置前，必须阅读本文档。

## 背景：为什么必须打破"Mock 幻觉"

`dsh-launcher` 曾因测试体系陷入"Mock 幻觉"而遭遇毁灭性环境 Bug：涉及进程调用（`Process.Start`）、
文件锁、外部环境的测试大量 Mock 了 OS 边界，CI 跑的全是"假集成"测试 → CI 全绿、一发布就崩溃
（`npm.cmd` 秒退 + 中文乱码）。

**铁律：测试必须触及真实 OS 边界，不能只验证内存中的逻辑流转。**

---

## 四大测试支柱

### 支柱一：真实 OS 交互集成测试（Real-OS Integration）

1. **去 Mock 化**：Mock 了 `Process`/`File`/`Registry` 的测试必须标记 `[Trait("Category", "Unit")]`，
   并**强制补充**对应的 `[Trait("Category", "RealOS")]` 真实集成测试。
2. **RealOsProcessTests.cs**：真实拉起进程，拦截乱码（GBK/UTF-8 冲突）、僵尸树清理（超时
   `Kill(entireProcessTree)`）、进程秒退等 OS 级 Bug。
3. **RealOsFileLockTests.cs / LoggerTests**：真实用 `FileShare.None` 独占锁死 `dsh.log`，
   断言日志 Fallback 到 `%TEMP%\dsh-launcher-fallback-{pid}.log`（绝不静默丢弃诊断信息）。

### 支柱二：带副作用断言的状态机测试（Side-Effect Lifecycle）

- 状态机测试不能只断言 `Assert.Equal(LifecycleState.X, app.State)`。
- **必须断言真实副作用**：注入的 `staleCleanup` 委托被调用且端口正确；阶段 0 的
  `BackgroundMaintenance` 真实落盘 `pending-update.json`/`window-state.json`。

### 支柱三：基于 TestHook 的真实 UI/E2E 自动化

- 拦截 WinForms 渲染、多显示器、弹窗文案等 UI 级 Bug。
- 通过 TestHook（`--ui-selftest` / `DSH_TEST_MODE=1`）注入假数据，UIA 抓弹窗文本。
- **断言弹窗含真实错误原因/版本号，绝非硬编码"下载失败"或乱码。**

### 支柱四：CI 分层与硬性门禁

| Stage | 内容 | 失败处理 |
|---|---|---|
| **Fast**（<10s） | Unit & Contract（`Category!=RealOS`） | 阻断 |
| **Real-OS** | `RealOsProcessTests` + `RealWorldNpmExecutionTests`；**真实安装 Node.js，绝不 Skip** | 阻断 |
| **UI/E2E** | TestHook UI 自动化 + 0px 间隙 + 弹窗文案 | 阻断 |

---

## 🐛 Bug 驱动复现铁律（Bug-Driven Test Policy）

> **铁律：每一个修复的 P0/P1 级环境 Bug，必须转化为一个"绝不使用 Mock、真实调用 OS 资源"的复现测试。如果无法编写真实复现测试，该 Bug 修复不予合并（merge）。**

### 适用场景（必须写复现测试）

- 进程调用（`Process.Start`）、`.cmd`/`.bat` shim 执行、`cmd.exe /c` 引号/编码陷阱
- 文件锁（`FileShare`）、日志 Fallback、僵尸进程树清理
- 环境变量/PATH 缺失、编码冲突（GBK vs UTF-8）、registry 依赖

### 复现测试标准

1. **零 Mock**：直接 `Process.Start` / `File.Open` / 真实文件系统，不注入 Fake。
2. **真实触发**：构造与线上完全一致的脚本/环境（如含中文的 `.cmd`、`FileShare.None` 锁）。
3. **明确断言**：进程 ExitCode 正常返回（不秒退）、输出无乱码（正则过滤非法 UTF-8 序列）、
   僵尸进程被杀干净。
4. **标记 Category=RealOS**，进 Real-OS Stage，CI 真实安装 Node 后运行。

### 命名约定

- 复现测试：`Regression_<Bug描述>`（如 `Regression_NpmCmd_Execution_And_Encoding`）。
- 真实 OS 集成：`RealOs_<场景>`。

---

## 变异测试（Mutation Testing，强烈推荐，可选门禁）

在 `Managers/` 和 `ShellLogic.cs` 上跑 Stryker.NET。故意改动致命逻辑（如 `Kill(true)`→`Kill(false)`、
`Encoding.UTF8`→`Default`），若变异存活（测试没发现），CI 标红。这戳破"覆盖率 100% 但测不出 Bug"的幻觉。

**接入步骤（test.ps1）**：
```powershell
# 安装 Stryker（一次性）
dotnet tool install -g dotnet-stryker
# 对核心纯逻辑 + 状态机做变异测试
dotnet-stryker --project src/DshShell/DshShell.csproj --test-project tests/DshShell.Tests/DshShell.Tests.csproj `
  --solution E:\dsh-launcher --open-report:false
```
**门禁**：变异存活率 > 阈值（如 10%）→ CI 标红。

---

## 变更管理 Checklist（测试改动必答）

- [ ] 我的测试是**真实 OS 交互**还是仍靠 Mock 兜底？
- [ ] 涉及进程/文件锁/编码的改动，是否补了 `Category=RealOS` 真实测试？
- [ ] 修复 P0/P1 环境 Bug 后，是否写了 `Regression_<Bug>` 零 Mock 复现测试？
- [ ] CI 的 Fast/Real-OS/UI 三层是否都有对应覆盖？
