# 架构债务台账（Architecture Debt Ledger）

> 2026-09 防臃肿整改（Phase 1–6）产出之一。**本文件只登记"本轮明确不做或做不到验证"的项**，
> 每条都写清：现状证据、为什么本轮不动、动它的前置条件、以及防止恶化的机器闸。
>
> 规矩：台账里的项不得靠"记得改"来管理——要么有闸拦着（列在最后一栏），要么在下次触碰同一
> 文件时顺手做掉并从本文件删除。**新增项必须带证据**（文件+符号，行号会漂移所以少写）。

---

## 1. 双 node.exe 解析器（D3）— 决定**不合并**

- 现状：`RuntimeResolver`（解析**宿主** node：便携版/系统 node，用于跑 npm-cli.js）与
  `Domain/DshDiscovery`（解析 **dsh 运行时**的 node）各有一套"注册表 hive 循环 + PATH 兜底"，
  代码近乎逐字相同。
- 为什么不合并：**两者解析对象不同**，合并会把"宿主 node 版本门槛（≥18）"与"dsh 运行时布局"
  两条独立演化路径焊死；一旦焊错，症状是首装链或更新链整体起不来，排查成本远高于两份重复代码。
  ADR-019 的"单一真相源"针对的是**版本比较**与**包名**（那两条已真合并：
  `ShellLogic.VersionPolicy.CompareVersions` 唯一实现 + 两个一行转发；
  `DshDiscovery.PackageScope`/`PackageShortName` 派生出 `PackageName`）。
- 前置条件：真要合，先给两份解析各写 RealOS 用例钉住现有差异（不同 hive、不同失败语义），
  再谈抽象。
- 防线：无专门闸；靠本条记录 + 两份各自的现有测试。

## 2. `ShellLogic.UpdateProxyPolicy.LocalProxyAlive` — 纯函数文件里开真 socket

- 现状：`ShellLogic.cs` 的 `UpdateProxyPolicy` 在自称纯函数的文件里 `new TcpClient` 探测本地代理，
  且**没有契约测试**。违反该文件自己的抽取规则（有生命周期状态的资源必须抽走）。
- 为什么本轮不动：它被 `G1b`（ShellLogic 不纯原语计数棘轮，基线 12）锁住不再增长；把它挪去
  Managers 需要新增一个"代理探测"归属，而代理策略目前只有这一个消费者。
- 前置条件：先补契约测试（注入一个可 Fake 的探针委托），再决定归属。
- 防线：`test.ps1` 的 **G1b**（≤12 且不纯行号列表逐条可见）。

## 3. `ShellLogic.RuntimeConfig` 读环境变量的包装层

- 现状：`ResolveTarget` 一类既有纯重载（有测试）也有读 `Environment.GetEnvironmentVariable`
  的薄包装（无测试，因为需要进程环境）。
- 为什么不动：纯重载已经是真相源，包装层只是取环境；把它挪走等于给一行代码建一个 Manager。
- 防线：G1b 的 `Process.Start(` / `TcpClient` 等模式不覆盖环境变量读取 → 该项**无闸**，
  新增环境变量读取请优先放到 `AppEnvironment`。

## 4. `Managers/WebViewManager` 的三个 per-window 静态

- 现状：`MainWeb` / `RecoveryNeeded` / `HiddenSince` 是 `public static`，而
  `docs/refactor-static-mapping.md` 对 `_mainWeb` 明写"不能是静态进程级，否则多窗场景引用混乱"。
  代码注释却自称符合该映射的 B 组。
- 为什么本轮不 un-static：un-static 会要求所有消费者（弹窗、恢复、托盘隐藏计时、健康监控页面层）
  都拿到"哪一个 WebView"的引用，那是一次独立的窗口化改造，不属于"防臃肿"范围。
- 前置条件：先决定弹窗/多窗的所有权模型（`WindowManager` 已经是弹窗工厂的注入口，是天然落点）。
- 防线：**G3 硬零**（下层不得回调 `Program.` 静态）+ **G1** 组合根体量棘轮，防止用静态绕路新增。

## 5. `BrowserProcessExited` 在 `WebViewManager.ProcessFailed` 无分支（已知独立缺陷）

- 现状：WebView2 的**浏览器进程**（非渲染进程）退出时，壳没有专门分支处理。若网页通知相关的
  WPN 崩溃落在浏览器进程，结果是**永久白屏且无自愈**（渲染进程崩溃才有重载路径）。
- 为什么本轮不修：它独立于 issue #25 与本次审计，且修法需要先确认浏览器进程退出后
  `EnsureCoreWebView2Async` 重建的正确姿势（现有测试覆盖不到）。
- 前置条件：真机复现一次浏览器进程退出（或 Fake `ICoreWebView2Environment`），再补分支 + E2E。
- 防线：无。**下一次触碰 WebViewManager 时应连带处理**（处理完请删除本条）。

## 6. `ProcessRunner.RunCapture` 的**第 6 份**副本留在 ShellLogic（D1 未完部分）

- 现状：进程三必须（双流排空 + 限时等待 + 超时 `Kill(entireProcessTree)`）此前有 6 份手写实现。
  Phase 5 · D1 已把其中 5 份并到 `Managers.ProcessRunner.RunCapture`
  （`Domain/DshDiscovery.ProbeVersionOutput`、`RuntimeResolver.IsUsableNode`、
  `UpdateChecker.ProbeGitDescribeVersion`、`DiagnoseExport.RunCapture` + `RunCaptureLines`）。
  最后一份在 `ShellLogic.cs` 的 npm 帮助类里。
- 为什么不动那一份：改成调用 `ProcessRunner.RunCapture` 会让**纯逻辑层反向依赖 Managers 层**
  （现在是 Managers → ShellLogic）。方向一旦反掉，ShellLogic 里就能塞任意 IO，比一份重复更贵。
- 前置条件：把那段采集本身从 ShellLogic 挪进 Managers（顺带清 G1b 的一处原语），再复用 helper。
- 防线：**G1b** 锁住 ShellLogic 的原语行数不涨。

## 7. pnpm 安装阶段不可取消（T6b 顺带发现）

- 现状：关窗时"正在构建更新"的确认框提供"强制关闭"。Phase 4 · T6b 把构建占用状态
  （`BuildInProgress` + 取消源）从组合根静态迁入 `DshUpdateManager`，并把取消令牌**真正**接进了
  `npm pack` 下载与 npm 安装回退两条 `RunNpmCommand` 路径（取消即 Kill 进程树）。
  **但 `ProcessRunner.RunPnpmInstall` 自己 `new Process` + 逐行读 ndjson，没有 ct 形参** →
  pnpm 阶段（常见路径，约 10~24 秒）打不断。
  修复前的状态更糟：`_buildCts` 从未传给任何进程，日志却写"build process canceled"——一句谎话。
- 现在留下的路径：取消请求会让 `BuildRuntimeFromTarball` **跳过 npm 回退**（不再续一条最长
  20 分钟的安装），tarball 按失败同形保全供下次启动免重下。
- 前置条件：给 `RunPnpmInstall` 加 `CancellationToken` + `ct.Register(() => p.Kill(entireProcessTree:true))`
  并在读循环里响应取消；需要一次真机长构建验证（网络门控 `-RealNet`）。
- 防线：`BuildStatusOutcomes.Outcome_NoBuildRunning_IsIdleAndCancelIsNoOp`（幂等语义），
  以及取消日志现在如实打印 `cancellation requested=True/False`。

## 8. 未钳制的裸 `/96f` DPI 换算 —— **已闭环（同日，D8 收尾）**

- 曾经：8 份钳制副本（B4 修的就是"其中一份漏了钳制"）+ **10 处完全没有钳制**的
  `form.DeviceDpi / 96f`（标题栏高度、`CustomTitleBar` 缩放、版本弹窗/探针的 `Rescale`）。
- 现在：`ShellLogic.DpiScale` 是唯一实现（`Of` 系数 / `Px` 设计像素→物理像素 / `Sanitize`
  "≤0 当 96"），12 个调用点全部改走它，src 代码行里的裸 `/96f` 换算**归零**。
- 闸：**G10 两条硬零**（第二份钳制字面量 = 0、裸 `/96f` = 0）。已反向验证：注入一次即红并点名违例行。
- 验证边界（不得夸大）：`--ui-selftest` 真机 `pass=True` 只证明**正常 DPI 下逐位不变**；
  "坏驱动给出 `DeviceDpi == 0` 时不再塌成 0px"由 `DpiScaleContractTests` 的边界例钉住，
  没有在真造的 0 DPI 屏上跑过。
- 保留编号以免交叉引用错位；本条已无待办。

## 9. `Program._shutdownInitiated` 与状态机 `ShuttingDown` 并存

- 现状：退出编排的幂等闸门是组合根的 `static bool`（在 G5 冻结清单里）。状态机里
  `LifecycleState.ShuttingDown` 已经存在并且 `RequestShutdown()` 会投递它。
- 为什么本轮不合并：关窗、托盘退出、看门狗强杀三条入口对"谁先跑清理"的时序依赖没有被测过；
  把物理互斥换成状态判定，最坏情况是清理跑两遍或一遍都不跑。
- 前置条件：先给 `BeginShutdownAsync` 补 Headless 用例（三入口并发调用只清理一次），再合并。
- 防线：**G5** 冻结清单（该名字若在，新增同类静态标量即红）。

## 10. `BuildStatusOutcomes` 的 BuildStatus 用例只校验枚举成员

- 现状：该类的 `Outcome_BuildStatus_*` 用例断言的是枚举成员/序数存在，**不**校验状态流转
  （标题栏需要真实窗体，属 `UiTestHookE2ETests` 的 E2E 面）。本轮已删掉其中一条
  `Assert.True(true, ...)` 的纯空断言，并补了一条真的读行为的
  `Outcome_NoBuildRunning_IsIdleAndCancelIsNoOp`。
- 前置条件：E2E 里驱动一次真实构建状态变化（`--ui-probe` + 标题栏取词）。
- 防线：无。**新增测试不得再写恒真断言**（AGENTS.md 反 Mock 幻觉条款）。

## 11. CHANGELOG `[Unreleased]` 段顶到 G7 上限 —— **已按用户授权放行（同日）**

- 当时状态：**G7** 规定锚点恰好 1 个且该段 ≤315 行；T5 收官时实测**正好 315**（并行会话的 DPI 批次
  填满，防臃肿整改自己一条都没写），加任何一条都会红。
- 用户 2026-09-19 的决定：**放宽上限并写明理由**。G7 现 ≤400 行，授权范围（一次、此数值、再抬需
  同等授权）写在该断言正上方；本轮整改已写成 `### 维护` 一节，段长实测 356。
- 闸没有被绕开：锚点仍 `-eq 1`，段长仍"低于上限时收紧到当下实测"，定版仍是真正的收口动作。
- 遗留：项目停更，可能永不定版；那就每轮按同等显式授权处理，**不许默默抬高手**。

## 12. `DshUpdatePipelineRealTests` 的过期注释

- 现状：文件中一段注释仍在描述"prerelease 序数比较"这一**已被删除**的旧行为（F1 已真修好：
  `VersionPolicy.CompareVersions` 唯一实现，prerelease 与 build metadata 均按 SemVer 处理）。
- 处理：下次触碰该文件时改正注释并删除本条。属"注释与实现分叉"，无行为影响。

## 13. 包名单一真相源（F9）—— **已闭环（同日）**

- 现在：`DshDiscovery.PackageScope` / `PackageShortName`（唯一定义）+ 由它们派生的
  `PackageName` / `PackageRelativeDir()`（安装布局路径段）/ `PackagePathMarker`（在既有路径串里
  定位本包目录）。用户可见的"手动执行 npm install -g …"文案、`node_modules\@deepseek-ai\dsh`
  路径段、bundle scope 前缀判定（`ShellLogic` 两处 `StartsWith`、`SafeProfileBuilder.DeepSeekScope`）
  全部改从常量取——`DshDiscovery` 注释里"路径段与提示文案一律从这里取"的承诺与现状终于一致。
- 闸：**G13 两条硬零**（除 `DshDiscovery.cs` 外的代码行里不得出现完整包名 / `"node_modules",
  "@deepseek-ai"` 路径段三连 / `StartsWith("@deepseek-ai/")`；`SafeProfileBuilder` 的 scope 常量
  不得再自立字面量）。已反向验证：注入一条即红并点名违例行。
- 合法保留：`SafeProfileBuilder` 的 `@deepseek-ai/dsh-base` / `-dsh-web-app` 是**不同包名**
  （核心 bundle 成员），闸按后缀豁免。

## 14. `BootHealthMonitor.AttachProcess` 的"attach 窗口"盲区（进程层可静默失去）

`AttachProcess` 在后台任务里跑；若服务进程**在 attach 落地之前**就退出，`GetProcessById(pid)` 抛
`ArgumentException: Process with an Id of N is not running.` → 该异常被 catch 成一条 Warn，
**不产生任何裁决**。这是 2026-08 误报根治有意为之的取舍（残留/陈旧 pid 曾把整监控打成 E2007 弹窗），
代价是：进程层在这条窄窗口里失明，真死要靠 HTTP 层（连续 2 次 miss）兜底，检测不丢但**晚几秒**。

- 实测证据（2026-09-19 本机一次性诊断）：给一个已退出的 pid 走 attach → `factoryThrew=True`、
  `verdictArrived=False`；给一个存活 pid → `E2007 / pid exit code=7` 正常出裁决。
- 它为什么值得记：`BootHealthMonitorRealOsTests` 里那条真实退出码用例原先只让子进程活 300ms，
  等于拿断言赌一次线程池调度。已改成"标志文件握手"（attach 确认落地后才放行退出），并把三个环节
  拆成三条独立断言，红灯名字直接指明断在哪一环。
- **诚实边界（2026-09-19）**：这条机制虽然本机可复现，但**不能拿来解释 CI 的那两次红**——把子进程
  寿命放宽到 5 秒后同一用例又红一次，而 `realos-test.yml` 的 `-v q` 吞掉了 Error Message，
  当前 token 没有 `workflow` scope 无法修日志 verbosity，原因仍未确证。本机侧 220 次执行全绿。
  **2026-09-20 更新（只解决工具面，没解决归因）**：verbosity 已修——Real-OS 层并入 `build.yml` 的
  real-os step，走 `test.ps1 -RealOsOnly`，`-v q` 换成 `-v minimal` 并保留 120 行，凭据也已补上
  `workflow` scope。**但"那两次红的确切原因"至今未确证**：并入后该层连绿三次（57 例 / 44–46s），
  样本仍不足以判定抖动已消失，下次它再红就从 `realos.log` 里读真实 Error Message，不再靠猜。
- 未做的更硬改法（记录理由）：让 `AttachProcess` 在 pid 已消失时直接判 E2007 ——会重新引入
  2026-08 那批误报（壳自己停服的窗口里 pid 必然"已消失"）；把调用方手里的 `Process` 对象传进来
  （生产侧 `ServiceManager` 确实持有）是正解，但那要给 `IBootProcessHandle` 加一条"由持有者提供
  退出码"的形状，属于独立改造，不在本轮范围。

## 15. 插件弹窗的初始尺寸不随启动屏倍率折算（本轮**故意**未做）

- 现状：`Program.CreatePopupForm` 写死 `ClientSize = new Size(900, 640)`，**没有**乘启动屏的 DPI
  系数；主窗那条 `1280 * scale`（Program.cs:604）它没有对应实现。175% 屏上弹窗一开就只有设计值的
  57% 大小，页面里 8pt 的字挤成一团。
- 本轮已修的相邻缺口：`DshShellForm.OnDpiChanged` 现在会在倍率变化时按 `RescaleWindowForDpi`
  等比放大窗口（弹窗同样受益）——但那要求**先发生一次跨屏/改倍率**，启动即在高倍率屏的情况仍然偏小。
- 为什么没顺手做：正解是"设计尺寸随启动 DPI 折算"这条规则要在主窗/弹窗/版本窗之间收敛成一处
  （现在主窗在组合根算、弹窗没算、版本窗走 `VersionDialogLayout`），属于与 D4/D8 同族的版式收敛，
  不该塞进一轮真机复测的修 bug 批次。
- 关联：`MinimumSize` 的重算被刻意限定为"只作用于**声明过**下限的窗体"（弹窗没声明 → 不被强行
  撑到 1400x1050）；等这条做完，两个条件应一起删掉。

## 16. CHANGELOG `[Unreleased]` 段第二次顶到 G7 上限（本轮**未抬高手**）

- 2026-09-20 真机复测批（4 处修复 + 1 条撤回）写完后实测 **400/400**，正好贴顶。
- 处理方式：把每条 bullet 压到"症状/实测数字/根因/修复/闸/测试"六要素各一行以内，**没有**抬上限
  ——按第 11 条记的授权口径，抬一次只对该数值有效，再抬需同等显式授权。
- 挪出去的内容：撤回项（"卡片跨屏不重算 DPI"是测量器造成的假缺陷）与"PASS 判据漏项造成假绿"
  两条**过程教训**不属产品变更，全文记在 `docs/SYSTEM_CAUSAL_MAP.md` 落点 8/9。
- 后果与出路：下一条 bug 记录进来必红。**已两次应验**：① 落点 10（就绪前插件崩溃的安全模式入口）
  要 8 行，靠重排既有记录吸收——4 处纯排版收行 + 1 处消重（`Regression_Issue25` 那条测试在"修复/
  测试"两节各写了一遍，删掉后把两节独有的事实合并回一处）；② 同日落点 10 改写 + 落点 11（卡片三处）
  要 16 行，同样只靠重排吸收（把 MonitorDpi 那条 18 行压到 15、issue #26 与通知契约面各收 1–2 行、
  删掉两处 bullet 之间的空行），零事实删除，段长回到 400/400。
  这就是"每加一条都要先还一行"的真实代价，而且**收行本身也在花工时**（本轮约 6 次编辑只为腾 16 行）。
  真正的收口动作是把 `[Unreleased]` 整段搬进版本标题（项目停更，可能永不做）；那就只能按第 11 条的
  口径逐次申请，或像本轮一样把细节留在因果地图、CHANGELOG 只留症状与去向。

## 17. 测试钩子可以"替换结论"而不是"替换输入"（本轮已收口一处，其余未审计）

- 已修：`DSH_TEST_UPDATE_SIGNAL` 的 dsh 分支直接 `return` 一个通知结论，绕过
  `UpdateNoticeFlowPolicy.Decide`。后果有两层——① 弹过"检测到 X（当前 X）"这种自相矛盾卡片；
  ② 更贵的一层是**它把 Decide 的 dsh 分支整条遮住了**，于是藏在里面的"无跳过记录=静默"
  哨兵撞车缺陷（全员收不到更新提示）多活了一整轮，且我此前所有"更新提示"真机绿灯都没测过产品代码。
  现在闸锁成"Outcome 构造唯一 + Decide 唯一入口"。
- 未做的部分（记债）：仓里其余 `DSH_TEST_*` 钩子（`DSH_TEST_NOTICE_CARD` /
  `DSH_TEST_FAKE_APPLY` / `DSH_TEST_INSTALL_MODE` / `DSH_TEST_ALLOW_GLOBAL_INSTALL` /
  `DSH_WEB_URL` 外部托管）没有一条系统性核对过"只替换输入还是替换了结论"。判据口径：
  **钩子生效时，被它覆盖的那段生产决策代码是否仍然被执行**；不执行的就是绿灯遮蔽源。
- 为什么值得单独记：这类遮蔽不会让任何测试变红，只会让"验过了"变成假话——正是本项目
  "声称强度必须匹配测量强度"要防的形态。

---

## 18. 本地与 CI 的 `[OK]` 条数差近一倍，**未归因**（2026-09-20）

- 现象：同一份 `scripts/test.ps1`，CI 干净检出打印 306–309 条 `[ OK ]`，本机在制工作树打印 580 条
  （上一轮会话留下的本地序列也是 565→580）。
- 两次失败的归因（都要记着，防止第三次又填一个"看起来合理"的解释）：
  ① 我写进 CHANGELOG 的"技术债扫描器没排除 `obj/`、本机多扫 140 个生成物"——复测方向相反：
     `DoEvents` 那类逐文件断言 **CI 56 条 / 本地 0 条**，已当场撤回（`ac913083`）。
  ② 309→306 我差点记成"删测试删掉了闸"——查了 test.ps1 里全部 `Get-ChildItem` 的目标目录后否掉：
     遍历 `tests/` 的只有新加的 1b 那两条闸，且它们扫的是分层文件，本轮一个都没删。
- 一条纯工具坑（就是它造出过我的假证据）：`cut -c1-70` 在 C locale 下按**字节**切，中文前缀之后的不同断言
  被折成同一行，`uniq -c` 于是报出一个不存在的重复倍数。
- 为什么不修：它不影响 CI 判定（两侧各自 0 条 `[FAIL]`），只是**人读日志时会误判覆盖变化**。
  前置条件：要么在一次性 worktree 里对同内容跑两遍逐段定位差异来源，要么改成"每条断言输出可机读的
  分区计数"再比。属于测量装置改进，不是产品缺陷。

## 19. 白名单/枚举型高扇出 Theory 本轮**判定保留**（审计建议砍）

- `SecurityBoundaryTests` 25 行可执行扩展名、`PathPolicyContractTests` 27 行路径注入字符、
  `IsAutoGrantedPermission` 14 行权限名——按生产代码的分支算，它们绝大多数走同一条 `_` 兜底分支。
- 为什么不砍：这类是**白名单**，每行只挡得住"有人把那一个字符/扩展名/权限单独放宽"的一次针对性改动。
  删掉任何一行，都要先证明该次改动仍会被别处抓住——证不出就不许删。
- 出路：如果将来要收，正确做法不是删行，而是把"白名单只有一个入口 + 名单文件本身有 owner 审阅"
  做成闸（同 G1b/D1 族的形状），再让枚举测试退化成 3–5 条代表值。

## 20. `e2e-geo` 现已每次 master 推送都跑，代价 5–6 分钟墙钟（2026-09-20 主动选择）

- 现状：它此前只被 PR / `v0.4.0` 触发，而本仓库从不发 PR ⇒ 自 2026-08-23 起从未运行；本轮把触发接回
  master（理由与判据见 `docs/00` 核心约束五第 7 条）。接上后首跑即绿。
- 代价：单次 push 多占 5–6 分钟（含 `npm install -g @deepseek-ai/dsh` + `dotnet publish` + 真 GUI 探针）。
  它与 build/ui-selftest **并行**，所以不推迟快线信号（快线仍在 ~2m40s 出结论）。仓库是 public，不占分钟额度。
- 出路（若要提速只能牺牲覆盖，需用户裁决）：给它加 `paths` 过滤（只在 src/脚本变更时跑），
  或降频为每日定时 + 发布前手动跑。**这是覆盖取舍，本轮不擅自做。**

## 21. 收窄 `e2e-multimon` 后，src 里 3 处 `Debug.Assert` 失去唯一执行机会（本轮留下的）

- 现状：`WindowManager.cs:86-88` 的三条 `Debug.Assert`（托盘 provider 未注入即报）此前唯一被全量跑到的
  地方是 `e2e-multimon` 第一步的 **Debug 配置**整套单测；该步已按目标收窄到多屏 13 条（见 CHANGELOG 同轮记录）。
- 实测口径：全仓真源码里的 `#if DEBUG`/`Debug.Assert` 只有这 3 处（`grep` 已排除 `obj/`、`bin/`），
  tests 里 0 处，csproj 无自定义 `DefineConstants`。所以 Debug 相对 Release 的独有覆盖 = 这 3 行。
- 为什么不直接删掉那 3 行：它们是 `TrayManager` 空壳被删（Phase 6）之后，"组合根忘了注入"的仅剩提醒；
  真跑到的路径另有 `Regression_Issue28_TrayExitRow.RealOs` 守着，但那条测的是行为不是"注入缺失"这一形状。
- 出路（正确解，属独立小改）：把这三条改成构造期的真异常或 `ArgumentNullException.ThrowIfNull`
  （Release 也生效），然后 `Debug.Assert` 归零——本仓库不该有一行只在 Debug 生效的守卫。

## 附：本轮新增的机器闸一览（防止上述债务再增长）

| 闸 | 拦什么 | 反向验证 |
|---|---|---|
| G1 / G1b | 组合根代码行数 ≤ 当下实测；纯函数文件的不纯原语行数 ≤ 当下实测 | 注入 2 行代码 → 红；2026-09-20 两次**天然红**：补安全模式入口时组合根 +24 行（2031>2007）→ 事务搬 Domain、文案搬 ShellLogic（2006）；修"答完是只弹回执"时又把失败正文整段下沉为纯函数（2003）。两次都是搬走，没抬过基线 |
| G2 | 单方法行数 ≤ 当下实测（含扫描器自检"必须找到一个成体量级的方法"，防解析退化成恒绿） | 注入 90 行方法 → 红 |
| G3 | 下层（Managers/Windows/Chrome/Domain/Lifecycle/Win32）回调 `Program.` 静态 = **硬零** | 写一处回调 → 红 |
| G4 | Manager 互不引用（兄弟引用计数棘轮） | 加一处 `WindowManager.Instance` → 红 |
| G5 | 组合根静态标量流程字段冻结清单（只减不增；现 7 项） | 新增同类字段 → 红 |
| G6 | 全仓空 `catch {}` 总数棘轮 | 加一个空 catch → 红 |
| G7 | CHANGELOG 单一 `[Unreleased]` 锚点 + 段长棘轮 | 复制锚点 → 红 |
| G8 | 文档↔代码一致性（docs/00 不得正面命令 cmd.exe；AGENTS.md 地图列全 Manager；映射表必须自称时效） | 删地图条目 → 红 |
| G9 | 已迁出的运行期事务符号**不得回流**组合根（方法名 + 静态字段 + 回滚无阻塞轮询 + 重挂导航的委托必须是投递形式） | 注入旧方法名/字段/`Thread.Sleep`/裸方法名委托 → 红 |
| G10 | DPI 钳制只在 ShellLogic.DpiScale 一处（硬零）；未钳制裸 `/96f` 处数棘轮 | 再抄一份钳制 → 红 |
| G11 | `taskkill` 启动点唯一 | 新增第二处启动点 → 红 |
| G12 | 短进程采集走 `ProcessRunner.RunCapture`（调用点 ≥5 为下限）；RunCapture 之外手写限时 `WaitForExit(n)` 处数棘轮（现 2） | 手写第二套采集 → 红 |
| G13 | npm 包名/作用域字面量只在 `DshDiscovery` 一处（硬零） | 在别处再写一次 `@deepseek-ai/dsh` → 红 |

**闸本身也会写坏（2026-09-20 抓到两起，都靠"造脏副本"暴露）**：① 判据里把 `$` 当字面量写进正则
——`$` 是行尾锚点，这条闸永远不会红；② 用整文件正则扫"某符号不得再出现"，结果被自己写的
"解释为什么不再有它"的注释命中，把闸跑断（本表 G9 早记过同一条，这次是 G1/卡片闸重犯）。
口径：**新闸必须拿"把旧写法造回来"的副本证明会红，且只扫代码行**。

G9–G12 的反向验证方式是**一次注入多项违规**（在 `WindowManager` 里临时放第二份 DPI 钳制 +
第二处 taskkill 启动 + 手写 `WaitForExit(5000)`），实测**恰好 4 条断言变红**且各自报出违例行，
随后删除探针并复核（`GATE-PROBE` 全仓命中 0）。没红过的闸等于没写。
