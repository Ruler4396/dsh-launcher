# DSH 版本通联渠道（DSH Compat Channel）

> 启动器与插件工作区之间的**版本契约交接规范**。本文件是入口指针；正文与账本在插件工作区。

**账本位置**：`E:\dsh-plugins\docs\compat\`
- [`README.md`](<../../dsh-plugins/docs/compat/README.md>) — 规范正文（角色分工、工作流、状态机、变更纪律）
- [`TOPIC-OWNERSHIP.md`](<../../dsh-plugins/docs/compat/TOPIC-OWNERSHIP.md>) — 事实类别 → 责任侧对照表
- `VERSIONS/<semver>/MANIFEST.json + NOTES.md` — 按 dsh 版本的机器可读事实账本与详录
- `TEMPLATES/` — 新版本建票模板（MANIFEST.json / ticket.md）

## 为什么有它

dsh（`@deepseek-ai/dsh`）是上游不受控软件。每个新版本（如 0.1.2-alpha.x）的契约变化需要调查；
如果启动器与插件各自调查一遍，同样的实验做两遍（token 栅栏、boot 签名漂移、profile 布局……）。
规范约定：**每个版本契约一次调查，证据进账本，另一方只做消费验证。**

## 启动器 agent 的责任

**做**——改动与 dsh 交互的代码（`Domain/DshDiscovery*`、`Managers/ServiceManager*`、`ShellLogic` 的
ServiceReadiness/BootGuard/ServiceLaunch/NpmHelpers 区、更新链、`WebViewManager`）涉及 dsh 版本行为变化时：

1. 先读账本：上文 `README.md` + 目标版本 `VERSIONS/<版本>/`；
2. 只调查 `TOPIC-OWNERSHIP.md` 归属 **launcher** 的事实类别（CLI/横幅/token/boot 签名/退出码/端口/布局/包管理/时序/版本比较）；
3. 每条事实带证据（契约测试名 / golden 样本 / 实机命令 / 对照实验），写进该版本 MANIFEST 的 launcher 节，
   并以行号引用本仓库 [`DSH_CONTRACT_INVENTORY.md`](DSH_CONTRACT_INVENTORY.md)（33 项静态注册表，sentinel 锁定）；
4. 在 NOTES.md 写「交接留言」，指明插件侧最该复验的点。

**不做**：

- 不调查归属 **plugins** 的事实类别（slots 目录、client 信封、settings 命名空间语义、session log 格式、
  webServer 端点层等）；
- 不在账本之外另建版本调查副本；本仓库只放指针与摘要，不复制账本正文。

**消费验证**（插件侧调查完、你只用）：对照证据跑最小冒烟（如 token 栅栏：裸地址期望 401、带 token 期望 200），
结果记入该版本 MANIFEST 的 `consumption.launcher`；冒烟失败 = 在 `openQuestions` 追加问题并更新 status，
**不是重新调查**。

## 与现有文档的关系

- `docs/DSH_CONTRACT_INVENTORY.md`：壳侧**静态逐行注册表**（每条 dsh 依赖点 + sentinel）；
  通联账本是**按版本的增量调查**。改契约行必须同步两边（账本条目 ref 到本表行号，本表新增行时若有
  版本行为变化，顺手在对应版本 NOTES 补一条）。
- `docs/SYSTEM_CAUSAL_MAP.md`：实机调查详录（如 #8 token 栅栏、#9 签名漂移）——账本存结论 + 指针。

## 变更纪律

- 规范与账本唯一写入口在 `E:\dsh-plugins\docs\compat\`；本文件只做指针与摘要。
- 修改通联规范条款需 launcher 与 plugins 两侧认可（compat/README.md 文末维护记录登记）。

*本指针建立：2026-08-29（通联渠道初版）。*