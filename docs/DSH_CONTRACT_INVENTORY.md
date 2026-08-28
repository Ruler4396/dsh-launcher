# dsh 契约清单（DSH Contract Inventory）——持续维护文档

> **性质**：活文档。列出 dsh-launcher 与外部系统 dsh（`@deepseek-ai/dsh`，不受本项目控制的上游）之间的**全部**依赖点。
> **谁维护**：任何改动"与 dsh 交互"的代码的 PR，必须同步更新本表（新增行 / 修改兜底列 / 更新位置行号）。
> **何时读**：动任何 `Domain/DshDiscovery*`、`Managers/ServiceManager*`、`ShellLogic` 的 ServiceReadiness/BootGuard/ServiceLaunch/NpmHelpers 区、更新链（`DshUpdateManager`/`StagedUpdate`/`UpdateDataGuard`）、`WebViewManager` 前端交互之前，**先查本表**。
> **评审基准日**：2026-08-28（深度审查批次一产出；发现编号 F1-F25 见 `docs/reviews/2026-08-28-quality-review.md`）。

## 使用规则

1. **强假设**（精确匹配/全量解析）与**实现级依赖**【标红】（dsh 内部细节，非公开契约）是升级脆弱点；dsh 发新版时按"风险=高/中"逐行过一遍。
2. 每行"哨兵"列 = 锁定该契约的测试。**样本测试失败 = dsh 变了**：先确认 dsh 行为变化是否可接受，再改样本（改样本即改契约，必须写进 commit message）。
3. 新增与 dsh 的交互点时，优先选"弱假设 + 超时兜底 + fail-open"形态，并在本表登记。

## 命令行参数 / 子命令

| # | 依赖项 | 强/弱 | 使用位置 | 风险 | 现有兜底 | 哨兵 |
|---|---|---|---|---|---|---|
| 1 | `web` 子命令 + `--host 127.0.0.1 --port N --no-open` | 强 | `ShellLogic.ServiceLaunch.BuildArgs`（ShellLogic.cs）；遗留 `scripts/start-dsh.vbs` | 中 | 直启失败→E2001 响亮 | `ServiceLaunchContractTests` |
| 2 | 根级 `--profile <name>`（与 `web` 互斥；只收目录名，无分隔符） | 强 | `BuildArgs` + `SafeProfileBuilder.SafeProfileName` | 中 | 安全模式双观测失败→E1011 + Deactivate 回正常模式 | `ServiceLaunchContractTests` + `SafeModeE2EOutcomes` |

## stdout / stderr 与前端消息

| # | 依赖项 | 强/弱 | 使用位置 | 风险 | 现有兜底 | 哨兵 |
|---|---|---|---|---|---|---|
| 3 | `dsh --version` stdout 版本号 | 弱（首个版本形态行：v 可选、2-4 段数字、可带 -pre/+meta；F3 已修复） | `DshDiscovery.ProbeVersionOutput` → `ExtractVersionLine` | 低 | 3s 超时杀树；找不到匹配行→null（fail-open，版本未知仍可启动） | `VersionProbeContractTests` + `DshDiscoveryProbeTests`（RealOS）+ golden ×2 |
| 4 | 启动错误标志关键字（npm ERR/EACCES/ECONNRESET…） | 弱包含 | `ShellLogic.ServiceReadiness.StartupErrorMarkers` × `ServiceManager.PollReadiness` | 低（**F2 已修复**：增量扫描只看入口后新增字节 + 壳行过滤 + 虚拟时钟宽限） | 15s 宽限 + 超时兜底 | `PollReadinessTests`（F2 回归门禁）+ `GoldenDshLogTests` |
| 5 | 运行期 boot 错误签名（plugin fatal / MODULE_NOT_FOUND…） | 弱包含 | `ShellLogic.BootGuard.BootErrorMarkers` × `BootHealthMonitor` 日志层 | 中（F6） | 增量扫描 + 壳行跳过（`IsShellAuthoredLogEntry`）+ `DSH_BOOT_SIGNATURES` 整表覆盖 | **golden 样本缺** |
| 6 | 前端好符号：`window.__DSH_BOOT__.version` ‖ `__ModuleLoader__.mode==="live"`【标红：dsh 前端内部符号】 | 强（JS 表达式） | `BootGuard.BootProfile.GoodSymbol` | **高（F5）** | Rendered 豁免（innerText≥60）+ AbsentThreshold 计票 + env 整体覆盖 | `BootGuardContractTests.DefaultGoodSymbol_CoversLegacyAndModernBootChains` |
| 7 | 前端坏签名文案（bootstrap facade is missing / dsh-boot-failed）【标红】 | 弱包含 | `BootProfile.BadSignatures` × `EvaluatePageProbe` | 中 | 改版失效=漏报不误杀；坏签名优先于好符号（S22 教训） | `BootGuardContractTests` 矩阵 |
| 8 | 前端→壳 postMessage 致命消息关键字【标红】 | 弱包含 ⚠️ 匹配过宽（F16） | `WebViewManager.InitializeAsync` WebMessageReceived | 中 | 失效→仅少一条安全模式触发通道 | 无（缺口） |
| 9 | `?safe_mode=1` URL 参数（仅 DSH_WEB_URL 外部托管模式） | 强 | `Program` 外部托管安全模式分支 | 低 | dsh 不响应=无操作 | 无（低价值） |

## 退出码语义

| # | 依赖项 | 强/弱 | 使用位置 | 风险 | 现有兜底 | 哨兵 |
|---|---|---|---|---|---|---|
| 10 | 服务进程退出码：0=优雅退出，非 0=异常 | 弱 | `BootHealthMonitor.OnProcessExited`（E2007） | 低 | Suspend 窗口豁免 + 幂等；exit 0 降级 Warn | `BootHealthMonitorTests` |
| 11 | pnpm `ERR_PNPM_IGNORED_BUILDS`（exit=1 视为成功） | 弱包含（stdout+stderr 双流） | `ShellLogic.UpdateProgress.IsPnpmIgnoredBuildsExit` | 低 | 失败沿源序列降级 npm | `UpdateProgressContractTests` |
| 12 | taskkill 退出码**不可信**（以端口/进程实况为准） | 设计决策 | `ShellLogic.ProcessManagement.KillServiceProcess` | 低 | 三阶段强杀 + 等待确认 | RealOS ×4（`Regression_BootLifecycle.RealOs`） |

## 端口 / 健康检查

| # | 依赖项 | 强/弱 | 使用位置 | 风险 | 现有兜底 | 哨兵 |
|---|---|---|---|---|---|---|
| 13 | 就绪信号 = `GET http://127.0.0.1:{port}/` 任意应答（含 4xx/5xx） | 弱 | `ServiceReadiness.IsHttpReady`（C3/ADR-005） | 低 | TCP 300ms + 预算 180s/360s + e2e 20s | `ContractTests.IsHttpReady_*` |
| 14 | 端口占用者进程名=node ⇒ 是 dsh | 弱且**过宽**（F4：会误伤用户自己的 node 程序） | `IsLikelyDshService` × `ServiceManager.ProbePort` | **高** | 端口归属双校验（仅防 PID 复用） | `ContractTests.IsLikelyDshService_*`；误伤场景无哨兵（缺口） |
| 15 | 3s HTTP 窗口内的"僵尸"判定 | 强（时序假设） | `ProbePort` | 中（慢启动健康服务可能被误杀） | Zombie→杀树重启自愈 | `ServiceManagerTests.ProbePort_*` |

## HTTP / 包管理

| # | 依赖项 | 强/弱 | 使用位置 | 风险 | 现有兜底 | 哨兵 |
|---|---|---|---|---|---|---|
| 16 | npm registry `/{pkg}/latest` 的 `version` 字段 | 弱（TryGetProperty） | `UpdateChecker.FetchLatestDshVersionAsync` | 低 | 失败→null→改用 `@latest` 标签安装 | `UpdateCheckerTests.FetchLatestDshVersion_*` |
| 17 | GitHub Releases `tag_name` / `body`(SECURITY) | 弱 | `UpdateChecker.FetchLatestLauncher*` | 低 | 失败静默（匿名限流 60/h 已知） | `UpdateCheckerTests` |
| 18 | 包名 `@deepseek-ai/dsh` | 强 | `DshDiscovery.PackageName` 等 3 常量 + 19 处字面量（F9） | 中 | npmmirror→官方源多源回退 | `NpmRegistryPolicyContractTests`（源序列）；包名无常量级哨兵 |
| 19 | npm pack tarball 命名 `deepseek-ai-dsh-{ver}.tgz` | 强+模糊兜底 | `StagedUpdate.LocateTarball` | 低 | 精确名→版本名→`*.tgz` 模糊匹配 | `V030FeaturesTests.LocateTarball_*` |
| 20 | npm/pnpm 参数契约（`--no-audit --no-fund` npm 专属 / pnpm 用 `--reporter=ndjson --ignore-workspace --config.node-linker=hoisted`） | 强 | `ProcessRunner.RunNpmCommand` / `RunPnpmInstall` | 中 | pnpm 失败降级 npm | `RealWorldNpmExecutionTests`（RealOS） |

## 文件系统约定

| # | 依赖项 | 强/弱 | 使用位置 | 风险 | 现有兜底 | 哨兵 |
|---|---|---|---|---|---|---|
| 21 | npm 全局布局 `%APPDATA%\npm\node_modules\@deepseek-ai\dsh` + `package.json.bin`（string/对象/首个 三态） | 强 | `JsEntryResolver.ResolvePackageEntry`；`DshDiscovery.ResolvePackageEntry` | 中 | 解析失败→`CanLaunchDirectly=false`→E2001 响亮 | `DshDiscoveryProbeTests`；解析形态 golden 样本缺 |
| 22 | SelfContained 布局 `runtimes\<ver>\node_modules\@deepseek-ai\dsh\{package.json,bin}` | 强 | `DshDiscovery.DiscoverSelfContainedRuntime`；`StagedUpdate.InspectRuntimeDir` | 低 | 完整性门禁 + AlreadyApplied/ReplaceStale 幂等 | `UpdateOutcomes.Regression_*` |
| 23 | `~/.dsh` 主目录 + `DSH_HOME` 覆盖 | 弱（dsh 生态标准） | `DshDiscovery.GetDataDir`；`Program.DshHomeDir` | 低 | env 可覆盖（测试重定向主通道） | 沙盒 Outcome 全系 |
| 24 | `profiles/<name>/package.json` 的 `dsh.profile.bundles`【标红：dsh 内部 schema】 | 强 | `ShellLogic.PluginConfig`；`SafeProfileBuilder.ResolveBundles` | 中 | 解析失败→按未装/最小核心（fail-open） | `V030FeaturesTests.IsLifetimePluginInstalled_*`；`SafeProfileBuilderTests` |
| 25 | dsh `settings.yaml` 的 `ui-theme.preference`【标红：内部格式，手写 YAML 段解析】 | 弱 | `Program.ReadDshThemePreference` | 低 | 失败→跟随系统主题 | 无（低价值） |
| 26 | 壳 `settings.json` 的 `serviceLifetime`（dsh-launcher-lifetime 插件写入） | 强（键+枚举 0/1/2） | `ShellLogic.PluginConfig` + `AppEnvironment.ReadLifetimeMode` | 低 | 插件缺失→purge + 回退 FollowWindow（E2011） | `V030FeaturesTests.ResolveEffectiveLifetime_*` ×6 |
| 27 | `.credentials.yaml` 跨版本单向迁移【标红：dsh 内部数据文件名】 | 强（硬编码白名单，F7：仅此一文件受保护） | `UpdateDataGuard.ProtectedRelativeFiles` | 中 | apply 首拍→失败按字节回滚→运行时隔离（E4003） | `UpdateDataGuardOutcomes` ×6 |

## 环境变量（进程间契约）

| # | 依赖项 | 强/弱 | 使用位置 | 风险 | 现有兜底 | 哨兵 |
|---|---|---|---|---|---|---|
| 28 | 服务子进程 env：`DSH_PORT`/`DSH_LOG`（dsh 是否读取**待人工确认**） | 弱 | `ServiceManager.ApplyServiceEnvironment` | 低 | `--port` 显式传参为主 | 无 |
| 29 | `DSH_PROFILE` env【死契约：唯一读者 start-dsh.vbs 已退出启动链，F8】 | — | `Program`（只写不读） | 低 | 无 | 删除而非测试 |

## 时序假设

| # | 依赖项 | 强/弱 | 使用位置 | 风险 | 现有兜底 | 哨兵 |
|---|---|---|---|---|---|---|
| 30 | 启动→就绪 180s（本地）/360s（npx 首装）、HTTP 3s、TCP 300ms、轮询 1s | 弱（超时兜底） | `ServiceReadiness.GetPollBudgetSeconds` × `PollReadiness` | 低 | 超时→E2002 + 僵尸清理 | `ContractTests.GetPollBudgetSeconds_*` |
| 31 | 版本探测 3s 超时 | 弱 | `ProbeVersionOutput` | 低 | 超时杀树 + memo | `DshDiscoveryProbeTests`（RealOS） |
| 32 | boot 探测：grace 12s / 探针 2s / 缺席阈值 5 / HTTP 连续 2 次 miss | 弱（可整表覆盖） | `BootProfile` × `BootHealthMonitor` | 低 | `DSH_BOOT_SIGNATURES` env 整体覆盖 | `BootGuardContractTests` |

## 调试协议

| # | 依赖项 | 强/弱 | 使用位置 | 风险 | 现有兜底 | 哨兵 |
|---|---|---|---|---|---|---|
| 33 | CDP `Runtime.exceptionThrown`（只采集不判定） | 弱 | `WebViewManager` 精确层 | 低 | enable 失败→仅禁用精确层 | 无（低价值） |

## 维护记录

| 日期 | 变更 | 依据 |
|---|---|---|
| 2026-08-28 | 初版建表（33 项契约，摘自深度审查批次一） | docs/reviews/2026-08-28-quality-review.md §1.4 |
| 2026-08-28 | #4 启动错误标志：F2 已修复（增量扫描+壳行过滤），哨兵补 PollReadinessTests/GoldenDshLogTests | remediation 分支批次 2 |
| 2026-08-28 | #3 dsh --version：F3 已修复（首个版本形态行，golden ×2） | remediation 分支批次 3 |
