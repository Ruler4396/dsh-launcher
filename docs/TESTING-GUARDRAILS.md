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

## 🧭 可红性铁律（Falsifiability，2026-09-20 新增）

> 判"测试是不是冗余"的**唯一**合法判据是：**它在什么输入下会变红**。条数不是判据——实测 1332 条纯逻辑用例
> 合计 29 秒，而 46 条真机用例占 54 秒；"1300 条太多了"这个直觉在时间与内容两个维度上都是错的。
> 真正需要清除的是**结构上不可能变红**的用例：它们比没有测试更贵，因为它们在给假的安全感定价。

### 四类实测抓到的"不可能红"形状（2026-09-20，本仓自己的样本）

1. **路径守卫型**——`var p = Path.Combine(AppContext.BaseDirectory, "x"); if (!File.Exists(p)) return;`
   而那个文件从来不在测试输出目录（`tests/**/bin` 下实测 0 个、csproj 无 `CopyToOutputDirectory`）⇒ 断言从未执行。
   加重的变体：断言的 token（`"--safe-mode"`）**在真实文件里根本不存在**（安全模式真形态是 `DSH_PROFILE` → 根级
   `--profile`）——只要真跑必红，那个 `return` 就是维持假绿的开关。
   **硬规矩**：断言仓库内真实文件一律走 `tests/DshShell.Tests/RepoFile.cs` 的 `RepoFile.Read(...)`，
   **找不到就抛**；`if (!File.Exists) return;` 这种写法在本仓库等同删除断言。
2. **断言自己型**——在测试里把生产判据/命令行拼接重写一遍，再 `Assert.Equal` 那个副本（生产代码一行没进，
   把被测函数整个删掉照样绿）。本仓还抓到更坏的一种：它抄的是生产**已删除且被 ADR 明令禁止**的旧规则，
   于是这条"回归钉"会把 bug 钉回来。**每个断言都必须能指出它调用了哪个生产符号。**
3. **零断言型**——`Assert.True(true, "...")`，或"调用了方法就算过"。**写了 0 条断言的用例必须判失败**，
   不豁免（豁免过一次，代价是一整轮把"什么都不判"当成覆盖）。
4. **逐字节重复型**——同一纯函数的高扇出 `[Theory]` 在两个文件各写一遍（实测 17 行矩阵里 15 行与另一份
   34 行矩阵逐字节相同，而"转发对不对"另有专门用例钉着）。**改一处判据要改两处 = 迟早不一致。**

### 高扇出 Theory 的审查口径（先回答这两个问题，再谈删）

- 生产代码**在哪些输入上真的分叉**？只走同一条分支的行，按覆盖算是灌水。
- 但**删掉某一行后，还有哪一次针对性的改动能被抓到**？白名单/黑名单枚举（25 行可执行扩展名、27 行路径注入字符）
  就属于这一类：多数行走同一条兜底分支，可每一行只挡得住"把那一个字符单独放宽"这一次改动。
  **证不出可安全删除就不要砍**——本仓 2026-09-20 的结论是这类全部保留，只删能证伪的。

### 交付与自证

- **删任何一条测试，必须在提交信息里回答"删掉之后 CI 少挡了什么"**；答不出"少挡什么"才允许删。
- 新写的判据/闸一律两向验证：真源码 = 期望结果，**造脏副本 = 反期望结果**。两向不对就不许写"已加闸锁定"。
  本轮新增的 2 条真断言就是这么过的：抹掉一处 `--no-open` → 红；把脚本整个挪走 → 抛 `FileNotFoundException` 红。
- **断言的消息参数是先求值的**：`Assert.True(cond, $"详情：{arr[0]}")` 在**成功路径**上就会越界抛异常
  （本轮我自己写的第一版当场被抓）。消息里要放数组内容就用 `string.Join`，不要索引。
- 比较 `[OK]` 条数做对账时，先固定"是哪棵树"：**同一份 test.ps1**，CI 干净检出打 306–309 条、本机在制工作树打 580 条
  （上一轮会话留下的本地序列也是 565→580）。差异**至今未归因**——本轮两次试图归因都失败：一次写"扫描器没排除 obj/"，
  复测发现 `DoEvents` 那类逐文件断言是 **CI 56 条 / 本地 0 条**，方向相反，已撤回；另一次把 309→306 猜成"删测试删掉了闸"，
  查了闸里所有 `Get-ChildItem` 目标目录后否掉。**口径：差异未查明前，不许据此宣称覆盖变多或变少，也不许写进任何权威文档。**

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
