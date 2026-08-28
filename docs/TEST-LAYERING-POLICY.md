# 测试分层配比与写作规约（Test Layering Policy）——持续维护文档

> **性质**：活文档（团队约定）。产出自 2026-08-28 深度审查维度四（测试策略与碎片化评估）。
> **关系**：与 `docs/TESTING-GUARDRAILS.md`（铁律，强制）互补——铁律说"必须做什么"，本文说"各层配比多少、怎么写、何时不写"。
> **维护**：每季度或测试体系重大变更时复核一次配比；改动测试组织结构（新增目录/搬迁文件）时更新 §6 现状清单。

## 1. 四层定义与配比目标

| 层 | 定义 | 目标配比 | 判定标准 |
|---|---|---|---|
| L1 纯逻辑单元 | `ShellLogic` 嵌套类纯函数、策略类（`*Policy`）、状态机（`LauncherLifecycle`）、版本比较、环境重定向纯决策。零 IO、零进程、毫秒级。 | **50–60%** | 状态机转换矩阵全覆盖；每个纯函数正/反/边界三态 |
| L2 组件集成（Headless） | 单个 Manager/服务可独立实例化，依赖经接口或委托注入（Fake），文件系统用真实临时目录（TempDir）。 | **25–35%** | 每个公共入口 3–5 场景（正常/边界/失败），不追行覆盖率 |
| L3 RealOS + 契约样本 | 真实进程/端口/文件锁/编码交互（`Category=RealOS`）+ **golden 样本**（dsh 真实输出片段存文件）。 | **10–15%** | 涉及进程/锁/编码的测试铁律强制 RealOS；每个 dsh 输出解析器至少一份 golden |
| L4 E2E（真实 GUI） | UIA/FlaUI 驱动真实窗口（`tests/DshShell.E2E`）。 | **≤5%（5±2 条冒烟）** | 仅"启动到就绪 / 切换驻留模式 / 退出清理"级冒烟 |

**当前实测（2026-08-28，约 450 用例）**：L1 ≈55% ✅ / L2 ≈33% ✅ / L3 ≈6% ⚠️（RealOS 质量高但 **golden 契约样本 = 0**，最大缺口）/ L4 ≈3% ✅。

## 2. 各层怎么写（防碎规则）

评价测试好坏的唯一标准：**锁定"对外可观察的行为"，不绑"内部实现步骤"**。

- **L1**：锁输入→输出契约；优先 `[Theory]` 参数化（范例：`UpdateCheckerTests.CompareVersions_ReturnsExpected` 17 例 Theory、`BootGuardContractTests` 误报防护矩阵）。转换矩阵"多而便宜"是正当形态。
- **L2**：经构造函数委托注入 Fake 探针（范例：`ServiceManagerTests` 注入 tcpProbe/httpProbe/pidLookup；`BootHealthMonitorTests` 注入 Fake 进程句柄与 1ms 间隔）。**本仓库禁用 Moq**——用手写 Fake（`Managers/TestFakes.cs` 范式），杜绝"断言 mock 被调用"式过度指定。
- **L3**：真实临时目录（`Outcomes/*:TempDir` 范式）+ 真实子进程 + 真实端口（范例：`Regression_BootLifecycle.RealOs` 对 `KillServiceProcess` 的活监听/错端口/死 pid/非 node 四连测）。golden 样本规程见 §5。
- **L4**：只断言用户可见结果（窗口矩形、进程退出、就绪），经 `--ui-selftest`/`--ui-probe`/`DSH_WEBVIEW2_READYSTATE` 钩子取证，禁肉眼。

**全仓已确立的纪律（写作时保持）**：零 Moq/零 Verify(Times.*)；零反射进私有成员（`InternalsVisibleTo=DshShell.Tests` 只用 internal）；命名 `方法_场景_期望`；断言关键片段而非完整长字符串。

## 3. 什么时候**不**写测试

1. 一次性脚本（`scripts/test-update-rc6.ps1`、`rollback-to-rc6.bat` 等演练脚本）。
2. 纯 UI 布局/绘制——用 `--ui-selftest` 几何断言代替单测（铁律：禁肉眼）。
3. 纯透传/委托属性（如 `WindowManager` 的注入委托属性本身——测注入方的行为，不测转发）。
4. 生成代码（CsWinRT/obj）。
5. 为覆盖率数字补测的简单属性。**覆盖率使用原则**：分支覆盖率只用来找"从未走过的路径"——尤其是异常路径（例：2026-08 审查发现的三处裸 catch 正是异常路径从未执行的证据）；不当 KPI、不设行覆盖率门禁。

## 4. 测试缺口登记（与审查发现联动）

| 缺口 | 对应发现 | 补测层 | 状态 |
|---|---|---|---|
| ~~`DshDiscovery.CompareVersions` Theory（rc.10>rc.9 等）~~ | F1 | L1 | ✅ 批次 1（`ShellLogicVersionPolicyContractTests` 24 例） |
| 就绪轮询日志判定（陈旧标志不污染/新增标志宽限后判死）+ 时间注入 | F2/F26 | L2+L3 | ✅ 批次 2（`PollReadinessTests` ×6，虚拟时钟毫秒级） |
| ~~golden 样本：StartupErrorMarkers / IsShellAuthoredLogEntry~~ | F31（部分） | L3 | ✅ 批次 2（`GoldenDshLogTests` ×6） |
| ~~golden 样本其余：MatchBootErrorSignature / EvaluatePageProbe / ProbeVersionOutput~~ | F31 | L3 | ✅ 批次 3+6（`VersionProbeContractTests` ×2 golden + `GoldenBootGuardTests` ×7，样本共 10 份） |
| F6 运行期签名匹配限定服务管道行 + F5 渲染豁免契约漂移遥测 | F5/F6 | L3 | ✅ 批次 6（`GoldenBootGuardTests.PipedLine_*` + Rendered 遥测断言） |
| "用户自己的 node 监听 3080 不被 Zombie 误杀" | F4 | L3 | ⏳ 批次 5 |
| ~~双实例并发启动（mutex/E1009）~~ | F21 | L1/L3 | ⏳ 批次 5 |
| 会话生命周期汇入状态机（RequestShutdown/WebViewCrashed/关机不拦截） | F13/F14/F15 | L1/L2 | ✅ 批次 4（LauncherLifecycleTests ×2 + LauncherAppScenarioTests ×3 + ShellLogicTests Theory ×5） |
| ~~Apply 中断于 Move/ClearPending 之间~~ | F23 | L3 | ✅ 批次 8（清账前移；收敛语义由既有 UpdateOutcomes 覆盖） |
| ~~`VerifySha256Async`（校验失败/官方源优先）~~ | — | L2 | ⏳ 统一整改期（可选，未执行） |
| 无账本孤儿服务兜底认领（node+HTTP 健康） | F19 | L3 | ✅ 批次 8（TryAdoptOrphanService 无账本分支） |

**建议合并/删除**：删除死契约面而非测试它（`SafeProfileBuilder.BuildSafeProfileArguments`、`StagedUpdate.Package`、`Program` 的 DSH_PROFILE 读写）；`V030FeaturesTests`（37 例）按域拆归位是组织性改进，非紧急。

## 5. golden 样本规程（L3 契约哨兵）

1. **目录**：`tests/DshShell.Tests/GoldenFiles/dsh/`；命名 `解析器名_场景.txt`（如 `StartupErrorMarkers_dshRuntimeEconnreset.log`、`ProbeVersionOutput_bannerPlusVersion.txt`）。
2. **来源**：真实 dsh 运行输出片段（统一日志行、`--version` 输出、崩溃 stderr），脱敏后入库；一个解析器至少 3 份（正常/出错/混排壳行）。
3. **失败语义**：样本测试失败 = **dsh 契约变了**，不是测试坏了。处理顺序：确认 dsh 变化可接受 → 更新样本 → commit message 必须写明"dsh 契约变更：X→Y"并同步 `docs/DSH_CONTRACT_INVENTORY.md` 对应行。
4. **禁 mock 文件系统**：样本经真实文件读取进入解析器（对齐 TESTING-GUARDRAILS）。

## 6. 现状清单（2026-08-28 快照）

- **L1**：`ShellLogicTests`(33)、`BootGuardContractTests`(30)、`ContractTests`(14)、`UpdateCheckerTests`(17)、`UpdateProgressContractTests`(13)、`UpdateFlowContractTests`(14)、`BootFailureRoutingContractTests`(10)、`UpdateGuardPolicyContractTests`(9)、`ProvisionPolicyTests`(8)、`ShellLogicServiceLifecycleTests`(8)、`LauncherLifecycleTests`(8)、`NpmRegistryPolicyContractTests`(5)、`StagedApplyPolicyContractTests`(4)、`ServiceLaunchContractTests`(2)、`V030FeaturesTests`(37，按域拆分待办)、Win32 几何/显示器(21)。
- **L2**：`BootHealthMonitorTests`(23)、`DiagnoseExportTests`(19)、`ServiceManagerTests`(11)、`LauncherAppScenarioTests`(9)、`LauncherAppTests`(7)、`SafeModeStateTests`(6)、`SafeProfileBuilderTests`(4)、`LoggerTests`(10)、`Outcomes/` 15 文件（≈57，真实临时目录物理断言）。
- **L3**：`Regression_BootLifecycle.RealOs`(4)、`Regression_ServiceOutputPipe.RealOs`、`DshDiscoveryProbeTests`(4)、`RealOsProcessTests`(5)、`RealWorldNpmExecutionTests`(3)、`RealOs/DshUpdatePipelineRealTests`(2)、`BootHealthMonitorRealOsTests`、`Regression_StalePidAttachTests`、`SecurityBoundaryTests`(4)。
- **L4**：`DshShell.E2E` 14 用例：DshUpdateFlow ×2、UiResponsiveness ×1、StartupLatency ×1、UiTestHookE2E ×2（ToggleMaximize/Shutdown）、MaximizeAcrossVirtualDisplay ×2 + 探针辅助。
- **强项（严禁削弱）**：KillServiceProcess RealOS 四连测、更新链 Outcome 十连测、update-guard 六连测、BootFailureRouting 三连测、BootGuard 误报防护矩阵。

## 附录：DSH_* 环境变量清单（45 处读取点、30 个变量）

**测试钩子（本仓自有，生产勿设）**：`DSH_TEST_MODE` `DSH_TEST_RESULT` `DSH_TEST_FAKE_APPLY` `DSH_TEST_SAFE_MODE_ANSWER` `DSH_TEST_INSTANCE` `DSH_TEST_FORCE_MANAGED` `DSH_TEST_CRASH` `DSH_TEST_ALLOW_GLOBAL_INSTALL` `DSH_TEST_SPLASH_DELAY_MS` `DSH_NO_UI` `DSH_E2E` `DSH_SANDBOX` `DSH_WEBVIEW2_READYSTATE` `DSH_SERVICE_CMD`（注入假服务命令）`DSH_VERSION`（版本覆盖钩子）。
**生产可配置**：`DSH_HOME`（主目录）`DSH_WEB_URL`（外部托管）`DSH_WEB_PORT` `DSH_PROFILE`（⚠️ 死契约，待删 F8）`DSH_NPM_MIRROR` `DSH_NPM_REGISTRY` `DSH_NODE_VERSION` `DSH_NODE_MIRROR` `DSH_PORTABLE_NODE_DIR` `DSH_WEBVIEW2_DATA` `DSH_LOG_LEVEL` `DSH_BOOT_SIGNATURES`（签名档覆盖）`DSH_USE_NEW_LIFECYCLE`（遗留开关）`DSH_LOG`（透传服务）`DSH_PORT`（透传服务）。
**规范**：新增测试钩子一律 `DSH_TEST_` 前缀；生产配置直接 `DSH_` 前缀；在本附录登记（本表是唯一清单）。

## 维护记录

| 日期 | 变更 |
|---|---|
| 2026-08-28 | 初版（摘自深度审查批次四；配比现状实测 + golden 规程 + 钩子清单） |
