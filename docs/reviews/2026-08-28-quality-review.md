# dsh-launcher 深度代码质量审查（2026-08-28）

> 审查性质：**只审查、不改代码**。每条发现均可指导一次小而安全、可回滚的提交。
> 审查批次：维度三（dsh 兼容边界）→ 维度二（状态机）→ 维度五（稳定性风险）。其余维度（一/四）待指令。
> 审查对照铁律：`AGENTS.md`、`docs/00-ARCHITECTURE-GUARDRAILS-MANDATORY.md`、`docs/TESTING-GUARDRAILS.md`。

## 批次进度

| 批次 | 维度 | 状态 |
|---|---|---|
| 1 | 维度三：dsh 兼容边界 | ✅ 完成（见 §1） |
| 2 | 维度二：状态机 | ✅ 完成（见 §2） |
| 3 | 维度五：稳定性风险专项 | ✅ 完成（见 §3） |
| 4 | 维度一：可测试性架构 | ✅ 完成（见 §4） |
| 5 | 维度四：测试策略与碎片化 | ✅ 完成（见 §5） |

> 编号约定：全部批次共用一套发现编号（F1…F32），便于汇总排序与逐条转化提交。
> 产出活文档：`docs/DSH_CONTRACT_INVENTORY.md`（dsh 契约清单）、`docs/TEST-LAYERING-POLICY.md`（测试分层配比）。

---

# §1 批次一：维度三（dsh 兼容边界专项）

## 1.1 执行摘要

dsh 交互面整体设计成熟：发现/启动/更新收敛到 `DshRuntimeIdentity` 单一真相源（ADR-024），就绪判定用"TCP+HTTP 任意应答"的弱假设、解析失败一律 fail-open，无解析崩溃路径。**最优先风险三项**：① `DshDiscovery.CompareVersions` 与 `UpdateChecker.CompareVersions` 双比较器漂移，prerelease 序数比较把 `rc.10` 判小于 `rc.9`——SelfContained 多版本共存时**更新后永远启动旧版**（P1）；② 就绪轮询的错误标志扫描吃**整个历史统一日志**而非增量，dsh 运行期合法输出的网络错误词（ECONNRESET 等）会跨会话污染，慢启动 >15s 即误判 E2003 并**误杀刚拉起的服务**（P1）；③ "端口占用者进程名是 node ⇒ 是 dsh"的弱身份判定，会把用户自己的 node 程序当僵尸强杀（P2）。

## 1.2 发现清单总表

| ID | 严重级 | 维度 | 位置 | 一句话问题 | 证据摘录 |
|---|---|---|---|---|---|
| F1 | **P1** | 版本比较 | `src/DshShell/Domain/DshDiscovery.cs:201-206` | 双版本比较器漂移：发现层的 prerelease 用序数比较，`rc.10 < rc.9` 判反，SelfContained 多版本共存时选错运行时 | `return string.CompareOrdinal(preA, preB);`（对照 `UpdateChecker.cs:133-141` 已按 SemVer 修复） |
| F2 | **P1** | stdout 关键字 | `src/DshShell/Managers/ServiceManager.cs:90-97` | 就绪轮询对**全量历史日志**做错误标志扫描，陈旧/良性的 `ECONNRESET` 等字样跨会话污染，慢启动 >15s 误判 E2003 并误杀服务 | `var content = ReadTextShared(logPath); if (ShellLogic.ServiceReadiness.LogShowsStartupError(content))` |
| F3 | P2 | stdout 格式 | `src/DshShell/Domain/DshDiscovery.cs:290-294` | 版本探测把**整段 stdout** 当版本号（注释声称"首个非空行"），dsh 输出任何附加行即产生脏版本 | `var version = ... output.Trim(); if (version is not null && version.Contains('.') && version.Any(char.IsDigit))` |
| F4 | P2 | 进程身份/时序 | `src/DshShell/ShellLogic.cs:1386-1394` + `src/DshShell/Managers/ServiceManager.cs:154-170` | "dsh 身份"判定仅凭进程名 `== "node"`；用户自己的 node 程序占用 3080 且 HTTP 慢即被 Zombie 强杀；3s HTTP 窗口也会把正在慢启动的健康 dsh 判成僵尸 | `return string.Equals(p.ProcessName, "node", StringComparison.OrdinalIgnoreCase);` |
| F5 | P2 | 前端内部符号 | `src/DshShell/ShellLogic.cs:314-324`、`src/DshShell/Managers/WebViewManager.cs:305-323` | 好符号/坏签名/postMessage 关键字全部押在 dsh **前端内部实现**（`__DSH_BOOT__`/`__ModuleLoader__`/文案）上，属实现级依赖（标红）；dsh 改版后判死能力静默失效（fail-open 到 Rendered 豁免） | `"(window.__DSH_BOOT__&&window.__DSH_BOOT__.version)\|\|(window.__ModuleLoader__&&...mode===\"live\")"` |
| F6 | P2 | 日志关键字 | `src/DshShell/ShellLogic.cs:188-195, 523-532` | 启动期与运行期共用一张含通用网络错误词的签名表，与 dsh 运行期输出混在同一日志文件，误报面持续存在（BootErrorMarkers 命中一次即判死 E2003） | `"EACCES", "ENOSPC", "ETIMEDOUT", "ECONNREFUSED", "ECONNRESET"` |
| F7 | P2 | 数据格式 | `src/DshShell/UpdateDataGuard.cs:45` | 回滚保护白名单仅 `.credentials.yaml` 一个文件；dsh 下次单向迁移其他共享文件时回滚不完整 | `public static readonly string[] ProtectedRelativeFiles = { ".credentials.yaml" };` |
| F8 | P3 | 死契约 | `src/DshShell/Program.cs:1132,1156,1618` | `DSH_PROFILE` 环境变量只写不读（唯一读者 start-dsh.vbs 已退出启动链），注释仍声称"使 vbs 回退路径生效"；同族死代码：`SafeProfileBuilder.BuildSafeProfileArguments`、`StagedUpdate.Package` | `Environment.SetEnvironmentVariable("DSH_PROFILE", ...)`（src 内 grep 零读取方） |
| F9 | P3 | 包名常量 | `DshDiscovery.cs:18` / `UpdateChecker.cs:18` / `StagedUpdate.cs:15` + 19 处字面量 | `@deepseek-ai/dsh` 有三个并存常量且 npm 参数串内嵌字面量 19 处（如 `Program.cs:1443`、`Program.cs:2138`），scope 变更时漏改即碎 | `install -g \"@deepseek-ai/dsh@{_preApplyIdentityVersion}\"` |
| F10 | P3 | 版本比较 | `src/DshShell/UpdateChecker.cs:149-159` | build metadata（`+`）未剥离而是混入 patch 段解析（`1.2.3+build` → 被当成 1.2.0）；四段版本第 4 段被丢弃。均为 fail-open，不阻断启动 | `var core = dash >= 0 ? s[..dash] : s;`（未处理 `+`） |
| F11 | P3 | URL 契约 | `src/DshShell/ShellLogic.cs:851-869` | `DSH_WEB_URL` 经 `GetLeftPart(UriPartial.Path)` 解析，**query 被静默丢弃**；外部托管服务若依赖 query 参数（token 等）会失效 | `return (uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), uri.Port);`（待人工确认外部部署是否使用 query） |
| F12 | P3 | env 契约 | `src/DshShell/Managers/ServiceManager.cs:291-298` | 向服务注入 `DSH_PORT`/`DSH_LOG` 但无文档证明 dsh 读取；若 dsh 未来自行写 `DSH_LOG` 文件会与壳的输出管道双写同文件 | `psi.EnvironmentVariables["DSH_PORT"] = port.ToString();` |

无 P0：未发现"解析崩溃/拒绝启动"类 fail-closed 路径——版本解析失败、npm 失败、bin 入口缺失均降级为响亮错误码或继续用旧版本。

## 1.3 P1 逐条详情

### F1（P1）双版本比较器漂移 → 更新后永远启动旧版

**为什么是问题**：`UpdateChecker.CompareVersions` 在 v0.4.0 专门修复过 prerelease 数值段比较（`UpdateChecker.cs:106-141`，回归测试 `UpdateCheckerTests.cs:175` 锁定 `"0.1.0-rc.10" > "0.1.0-rc.9"`），但 `DshDiscovery.CompareVersions`（`DshDiscovery.cs:185-207`）保留了旧的 `string.CompareOrdinal(preA, preB)`。它正是 SelfContained 运行时挑选的唯一裁决（`DshDiscovery.cs:117`：`CompareVersions(version, bestVersion) > 0`）。Apply 路径从不删除旧版本目录（`DshUpdateManager.ApplyPending` 只 Move 新目录进 `runtimes\`，仅失败才隔离），所以第二次 SelfContained 更新起 `runtimes\0.1.1-rc.9` 与 `runtimes\0.1.1-rc.10` 必然共存——序数比较下 `'1' < '9'`，发现层永远选 rc.9。用户视角即"进度条 100%、重启、版本没变"；唯一的痕迹是 `LogPostApplyIdentity` 的一条 Warn（`DshUpdateManager.cs:376-391`）。dsh 现役发布线就是 rc.x prerelease（代码注释与测试多处实证 rc.2/rc.6/rc.7/rc.8），命中概率不是边缘。

**最小修复**：`DshDiscovery.CompareVersions` 整体删除，`DiscoverSelfContainedRuntime` 内改调 `UpdateChecker.CompareVersions`（两处签名兼容：均 `(string?, string?) → int`）。单提交、可回滚。

**理想修复**：把 SemVer 比较提取为 `ShellLogic` 纯函数（唯一实现），`UpdateChecker` 与 `DshDiscovery` 同源引用；顺带补 build metadata 剥离（F10）。

**自评风险**：会改变"多版本共存时选谁"的行为（这正是修复目的）。验证步骤：① 给比较器补 Theory（`rc.10 vs rc.9`、`0.1.1 vs 0.1.1-rc.2`、`1.0.0+build`）；② RealOS 场景：`runtimes\` 放两个 rc 版本目录，断言发现层选中语义化更大的那个（参照 `tests/DshShell.Tests/RealOs/DshUpdatePipelineRealTests.cs:120-125` 已在用"生产同款"比较器，改造后该测试自动跟随）。

**应补测试（防碎设计）**：直接锁**比较函数的公共行为**（输入→序数结论），不锁内部分段实现；再补一条 Outcome：`runtimes\` 双版本目录 + `DiscoverCurrentRuntime()` 返回新版本的物理事实。

### F2（P1）就绪轮询吃全量历史日志 → 跨会话污染误杀服务

**为什么是问题**：`PollReadiness` 每 5 秒把统一日志**整文件**读出来喂给 `LogShowsStartupError`（`ServiceManager.cs:88-97`）。统一日志是壳 JSON Lines + dsh 原始输出混排的**追加型**文件（`PipeServiceOutputToUnifiedLog`，`ServiceManager.cs:329-370`），轮转仅在 >30MB 或 >3 天时发生（`Logger.cs` `ShouldRotate`）。而签名表含 `ECONNREFUSED/ECONNRESET/ETIMEDOUT/EACCES` 这类**通用网络词**（`ShellLogic.cs:188-195`）——dsh 是 AI Harness，运行期对上游 API 的任何一次重试告警都会把这些词写进日志，且永久驻留。时序：`i=0` 首轮即命中（`lastLogCheck=DateTime.MinValue`）→ 15 秒宽限（`ServiceManager.cs:122-126`）→ 服务若 15 秒内没就绪即返回 `"logerror"` → `LauncherApp` 判 `ReadinessTimedOut` → `HandleStartupFailure`（`Program.cs:1751-1764`）**强杀刚拉起、可能正健康启动中的服务**并弹 E2003。最坏形态：首次安装（npx/网络路径天然 >15s）遇上带陈旧 npm 错误的日志 = 永久 E2003 循环。对照之下，`BootHealthMonitor` 的日志层做对了——增量偏移扫描 + 只扫监控起点之后 + 跳过壳自写行（`BootHealthMonitor.cs:230-277, 259`），F2 的修复范文本仓库里已有。

**最小修复**：`PollReadiness` 启动时记录 `new FileInfo(logPath).Length` 作为扫描起点，此后按偏移增量读（复用 `BootHealthMonitor.ReadLogIncrementAsync` 的模式），并把壳自写行经 `ShellLogic.BootGuard.IsShellAuthoredLogEntry` 过滤。单文件改动，语义收窄、不改正常路径。

**理想修复**：服务输出行带 `[dsh]` 前缀（这是**壳自己加的**格式，`ServiceManager.cs:336`），签名匹配限定在 `[dsh]` 前缀行内——从此壳的错误文案（如 E1012 详情内嵌的 npm tail，`DshUpdateManager.cs:116-119`）天然出局。

**自评风险**：收窄判定面后，**真实**启动失败的提前判死可能晚到（退化为 180s/360s 超时兜底）。验证步骤：① RealOS 测试——先写含陈旧 `npm ERR` 的日志，再拉一个 20 秒后才就绪的假服务（`DSH_SERVICE_CMD` 注入），断言 PollReadiness 返回 `ready` 而非 `logerror`；② 反向用例——本轮新增的错误行仍在宽限后触发 `logerror`。

**应补测试（防碎设计）**：golden 样本文件（真实 dsh.log 片段：正常启动行 / 含 ECONNRESET 的 dsh 运行期告警行 / 壳 E1012 JSON 行）作为固定输入喂 `LogShowsStartupError` + 增量扫描器；样本失败 = 判定语义变了，改样本即改契约。**不要** mock 文件系统——按 TESTING-GUARDRAILS 用 `Category=RealOS` 真实临时文件。

## 1.4 《dsh 契约清单》

强假设=精确匹配/全量解析；弱假设=包含匹配/尽力而为。【标红】= 实现级依赖（dsh 内部细节，非公开契约），升级即碎。

| # | 依赖项 | 依赖类型 | 使用位置 | 强/弱 | 风险 | 现有兜底 |
|---|---|---|---|---|---|---|
| 1 | `web` 子命令 + `--host 127.0.0.1 --port N --no-open` | 命令行参数 | `ShellLogic.cs:822-829`；遗留 `scripts/start-dsh.vbs:58` | 强 | 中 | 直启失败→E2001 响亮；`ServiceLaunchContractTests` 锁 `--no-open` |
| 2 | 根级 `--profile <name>`（与 `web` 互斥，仅收目录名无分隔符） | 命令行参数 | `ShellLogic.cs:825-827`；`SafeProfileBuilder.cs:30,139` | 强 | 中 | 安全模式双观测失败→E1011 + Deactivate 回正常模式 |
| 3 | `dsh --version` stdout 版本号 | stdout 格式 | `DshDiscovery.cs:264-301` | 弱（含`.`且有数字即收） | 中（F3） | 3s 超时→Kill 树；失败→null，版本未知仍可启动 |
| 4 | 就绪信号 = `GET /` 任意应答（含 4xx/5xx） | 端口/健康检查 | `ShellLogic.cs:202-234`（C3/ADR-005/006） | 弱 | 低 | TCP 300ms 超时；预算 180s/360s；e2e 20s |
| 5 | 启动错误标志关键字（npm ERR/EACCES/ECONNRESET…） | stdout/stderr 关键字 | `ShellLogic.cs:188-195` × `ServiceManager.cs:88-126` | 弱包含 | **高（F2）** | 15s 宽限 + 超时兜底；无增量、无前缀过滤 |
| 6 | 运行期 boot 错误签名（plugin fatal/…） | 日志关键字 | `ShellLogic.cs:523-532,562-569` × `BootHealthMonitor.cs:236-277` | 弱包含 | 中（F6） | 增量扫描 + 壳行跳过 + `DSH_BOOT_SIGNATURES` 整表覆盖 |
| 7 | 前端好符号：`window.__DSH_BOOT__.version` / `__ModuleLoader__.mode==="live"`【标红】 | 前端内部符号 | `ShellLogic.cs:314-316` | 强（JS 表达式） | **高（F5）** | Rendered 豁免（innerText≥60）+ AbsentThreshold 计票 + env 覆盖 |
| 8 | 前端坏签名文案（bootstrap facade is missing 等）【标红】 | DOM/err 文本 | `ShellLogic.cs:319-324,449-505` | 弱包含 | 中 | 改版失效=漏报不误杀；坏签名优先于好符号（S22） |
| 9 | 前端→壳 postMessage 致命消息关键字【标红】 | WebMessage 格式 | `WebViewManager.cs:305-323` | 弱包含 | 中 | 失效→仅少一条安全模式触发通道；页面/日志层兜底 |
| 10 | `?safe_mode=1` URL 参数（仅外部托管） | 前端行为契约 | `Program.cs:636-640` | 强 | 低 | dsh 不响应=无操作 |
| 11 | 退出码语义：0=优雅退出，非 0=异常 | 退出码 | `BootHealthMonitor.cs:194-213`（E2007） | 弱 | 低 | Suspend 窗口豁免 + 幂等去重；exit 0 降级 Warn |
| 12 | 端口占用者进程名=node ⇒ 是 dsh | 进程身份 | `ShellLogic.cs:1386-1394` × `ServiceManager.cs:154-170` | 弱且**过宽** | **高（F4）** | 端口归属双校验（防 PID 复用）；无法排除用户自己的 node |
| 13 | pnpm `ERR_PNPM_IGNORED_BUILDS` exit=1 视为成功 | 退出码 | `ShellLogic.cs:613-616`（双流判定） | 弱包含 | 低 | 失败沿源序列降级 npm |
| 14 | npm registry `/{pkg}/latest` 的 `version` 字段 | HTTP JSON | `UpdateChecker.cs:86-100` | 弱（TryGetProperty） | 低 | 失败→null→改用 `@latest` 标签安装 |
| 15 | GitHub Releases `tag_name`/`body`(SECURITY) | HTTP JSON | `UpdateChecker.cs:30-72` | 弱 | 低 | 失败静默（限流 60/h 已知） |
| 16 | 包名 `@deepseek-ai/dsh` | 包管理契约 | 3 个常量 + 19 处字面量（F9） | 强 | 中 | npmmirror→官方源多源回退 |
| 17 | npm pack tarball 命名 `deepseek-ai-dsh-{ver}.tgz` | 文件系统约定 | `StagedUpdate.cs:128-151` | 强+模糊兜底 | 低 | 精确名→版本名→`*.tgz` 模糊匹配三级 |
| 18 | npm 全局布局 `%APPDATA%\npm\node_modules\@deepseek-ai\dsh` + `package.json.bin` | 文件系统约定 | `JsEntryResolver.cs:67-106`；`DshDiscovery.cs:338-376` | 强 | 中 | bin 三态解析（string/`dsh` 键/首个）；失败→`CanLaunchDirectly=false`→E2001 响亮 |
| 19 | SelfContained 布局 `runtimes\<ver>\node_modules\@deepseek-ai\dsh\{package.json,bin}` | 文件系统约定 | `DshDiscovery.cs:94-136`；`StagedUpdate.cs:221-286` | 强 | 低 | 源完整性门禁 + AlreadyApplied/ReplaceStale 幂等 |
| 20 | `~/.dsh` 主目录 + `DSH_HOME` 覆盖 | 文件系统约定 | `DshDiscovery.cs:176-183`；`Program.cs:79-87` | 弱（生态标准） | 低 | env 可覆盖 |
| 21 | `profiles/<name>/package.json` 的 `dsh.profile.bundles`【标红】 | dsh 内部配置格式 | `ShellLogic.cs:1100-1197`；`SafeProfileBuilder.cs:103-136` | 强（内部 schema） | 中 | 解析失败→按未装/最小核心（fail-open，检测能力丢失） |
| 22 | dsh `settings.yaml` 的 `ui-theme.preference`【标红】 | dsh 内部配置格式 | `Program.cs:2635-2666`（手写 YAML 段解析） | 弱 | 低 | 失败→跟随系统主题 |
| 23 | 壳 `settings.json` 的 `serviceLifetime`（dsh-launcher-lifetime 插件写入） | 自有插件契约 | `ShellLogic.cs:1204-1250`；`AppEnvironment.cs:183-236` | 强（键+枚举） | 低 | 插件缺失→purge + 回退 FollowWindow（E2011 留痕） |
| 24 | `.credentials.yaml` 跨版本单向迁移【标红】 | dsh 内部数据格式 | `UpdateDataGuard.cs:45` | 强（文件名硬编码） | 中（F7） | apply 首拍 + 失败按字节回滚 + 运行时隔离（E4003） |
| 25 | 服务 env：`DSH_PORT`/`DSH_LOG` | env 契约（未文档化） | `ServiceManager.cs:291-298` | 弱 | 低（F12） | `--port` 显式传参为主（待人工确认 dsh 是否读取） |
| 26 | `DSH_PROFILE` env | 死契约 | `Program.cs:1132` | — | 低（F8） | 无读取方 |
| 27 | 时序：启动→就绪 180s/360s、HTTP 3s、TCP 300ms、轮询 1s | 时序假设 | `ShellLogic.cs:274-287`；`ServiceManager.cs:70-132` | 弱（超时兜底） | 低 | 超时→E2002 + 僵尸清理；NpxCache 首装放宽 360s |
| 28 | 时序：3s HTTP 窗口内的"僵尸"判定 | 时序假设 | `ServiceManager.cs:154-170` | 强（3s 假设） | 中（F4 延伸） | Zombie→杀树重启自愈；但会杀正在慢启动的健康 dsh |
| 29 | CDP `Runtime.exceptionThrown` | 调试协议 | `WebViewManager.cs:326-341` | 弱（只采集不判定） | 低 | enable 失败→仅禁用精确层 |

**反脆弱总评**：所有 dsh 输出解析点均有 try-catch 且 catch 后有明确路径（null→fail-open / 响亮错误码），**无一处解析崩溃**；`ResolveBinEntry`、`EvaluatePageProbe`、`ReadPending` 等"解析失败返回默认值"都记了日志或属预期降级。唯一系统性弱点是 #5/#6 的关键字误报面（F2/F6）与 #12 的身份过宽（F4）——三者都不是"碎"，而是"误伤"。

## 1.5 测试缺口地图（限 dsh 契约相关）

**已有（质量良好）**
- `UpdateChecker.CompareVersions` 全矩阵 Theory（含 rc.10>rc.9、prerelease 规则）——但只锁了更新检查那个比较器；
- `ServiceLaunch.BuildArgs` 契约测试（`--no-open`/profile 形态）；`BootGuardContractTests` 对 `EvaluatePageProbe` 的误报防护矩阵（好/坏/双编码/渲染豁免）相当扎实；
- `DshDiscoveryProbeTests`（RealOS：版本探测超时杀树、memo 失效）；`ServiceManagerTests` 端口三态注入式 Fake；`UpdateProgress`/`NpmRegistryPolicy`/`StagedApplyPolicy`/`UpdateGuardPolicy` 纯策略契约测试。

**缺失（建议新增，按优先级）**
1. `DshDiscovery.CompareVersions` 直接 Theory（F1 门禁）——补上即防双比较器再次漂移；
2. 就绪轮询日志判定的 RealOS 用例：陈旧错误标志不污染新一轮（F2 门禁）+ 本轮新增错误仍触发 logerror；
3. 版本探测 golden 样本：多行 stdout / banner+版本 / 纯 banner（F3 门禁）——真实子进程输出片段存文件；
4. "非 dsh 的 node 进程监听 3080" RealOS Outcome：断言不被 Zombie 清理（F4 门禁，真实 node 子进程即可构造）；
5. `LogShowsStartupError`/`MatchBootErrorSignature`/`IsShellAuthoredLogEntry` 的 golden 样本测试（真实 dsh.log 片段三分类：dsh 输出 / 壳 JSON 行 / 混排）——充当 dsh 变更哨兵；
6. `DSH_WEB_URL` 带 query 的 `ResolveTarget` 行为锁定（F11，先由人工确认期望语义再写）。

**建议合并/删除**
- 删除死契约面（勿写测试）：`SafeProfileBuilder.BuildSafeProfileArguments`（零引用）、`StagedUpdate.Package`（零引用）、`Program.cs` 的 `DSH_PROFILE` 读写（F8）；
- `UpdateCheckerTests` 中 `FetchLatestLauncherVersion_*` 六个 Fact 与 `FetchLatestLauncherRelease_*` 高度同构，可参数化合并（P3 观察，收益低）。

## 1.6 铁律冲突项

1. **AGENTS.md 与架构文档自相矛盾（文档级冲突）**：AGENTS.md 快速合规清单要求"调用外部进程（npm/node/wscript/taskkill）→ `cmd.exe /c` 包装"，但 ADR-021/ADR-024 与代码明确**禁止** cmd.exe 中间层（`JsEntryResolver.cs:8-17`；`ProcessRunner.cs:49-51`；`ShellLogic.cs:1531-1533`）。实际代码是对的，AGENTS.md 表格是旧版铁律残留——会误导下一个 Agent 把 cmd.exe 包装加回来。建议修订 AGENTS.md 该行。
2. **`ServiceManager`/`UpdateChecker`/`RuntimeResolver` 自建 `HttpClient`**：`WebRuntimeInstaller.CreateHttpClient` 注释自称"网络调用的唯一出口"（`WebRuntimeInstaller.cs:11`），实际四处各建实例且 UA 不统一。统一走工厂为低成本收敛（P3）。
3. **`ServiceLifecycleOps.RecordServicePid` 用裸 `File.WriteAllText`**（`ServiceLifecycleOps.cs:23`）写 `service-pid-{port}.txt`：属壳自管状态文件，铁律只点名 pending-update.json/window-state.json 必须原子写，此处擦边；已有 `int.TryParse` 失败→清文件的容错（`ServiceLifecycleOps.cs:71-75`），实际风险低（P3 观察，不强制整改）。

---

# §2 批次二：维度二（状态机）

## 2.1 执行摘要

启动段生命周期有一台**高质量显式状态机**（`LauncherLifecycle`：转移表 + Fail-Fast 抛 `InvalidOperationException` + Headless 可测，测试覆盖正常/超时/失败/幂等/非法转移），`BootHealthMonitor` 的三态监控状态机同样合格。**结构性问题是"状态机只管启动、不管运行"**：`ShutdownRequested` 触发器定义了但全仓零 `Fire` 调用、`HandleWebViewCrashed` 零调用——Running 之后的会话状态（服务归属、构建中、关停中、重启中）全部由 `Program` 的 7+ 个静态字段组合表达，恰是铁律明令禁止的形态。三个 P2：运行期状态机旁路（F13）、关窗与后台重启流的并发未定义（F14）、系统关机/注销路径未定义（F15）。无 P1（非法转移未被静默吞：状态机抛错；运行期组合各有 ad-hoc 守卫，未发现可稳定触发的状态腐坏）。

## 2.2 状态显式化核查（检查项 1）

**显式枚举（好的部分）**
- 生命周期：`LifecycleState` 9 态 + `LifecycleTrigger` 11 触发器 + 显式转移表（`Lifecycle/LauncherLifecycle.cs:4-75`）。
- 启动健康：`BootHealthState`（Pending/Healthy/Failed 吸收态）+ `_suspended/_stopped/_pageArmed/_promptConsumed` 均为锁内私有字段且有明确语义（`Lifecycle/BootHealthMonitor.cs:107-123`）。
- 驻留模式：`ShellLogic.ServiceLifetime` 枚举（AlwaysOn/Tray/FollowWindow），来源为磁盘配置即时读取（非内存状态，每次 `ReadLifetimeMode()` 重读）。
- 端口三态：`ServicePortState`（Closed/Healthy/Zombie/Foreign）。

**坏味道：运行期状态由 bool/字段组合隐式表达（P2=F13）**

`Program.cs` 的会话状态字段组（行号为当前工作区快照）：

| 字段 | 行 | 语义 |
|---|---|---|
| `_serviceStartedByShell` | 44 | 服务归属（本壳拉起） |
| `_servicePid` | 55 | 服务 PID 内存缓存（与磁盘 pid 文件双源） |
| `_isBuildInProgress` | 841 | 更新构建中（volatile bool） |
| `_applyRestartDeferred` | 1018 | "稍后"询问标记 |
| `_applyRestartPendingVersion` | 1021 | 待询问的更新版本 |
| `_updateRollbackArmedVersion` | 1026 | 回滚闸门武装标记 |
| `_shutdownInitiated` | 2448 | 退出编排进行中 |
| `_pendingUpdate/_pendingLatest/_pendingForm` | 832-835 | 更新提示状态 |

"服务生命周期（未启动/启动中/运行中/停止中/更新中）"没有对应枚举——它的真实形态是 `(LauncherLifecycle 启动段) + (上述字段) + (磁盘 pid 文件) + (端口实况)` 的四源拼合。**缺失的显式状态**：`Starting/Running/Stopping/Updating/ShutdownPending` 的运行期会话状态机。

**非法组合清单与可达性核查**：

| 组合 | 是否排除 | 依据 |
|---|---|---|
| `ServerManagedExternally && _serviceStartedByShell` | ✅ 排除 | `Program.cs:339`（adopt 前置守卫）、外部托管永不进启动分支 |
| `_shutdownInitiated && _isBuildInProgress`（强制关窗） | ⚠️ 已定义 | `Program.cs:480-503`：置 `_buildCts.Cancel()` + `_isBuildInProgress=false` 后进关停；构建线程后续 UI 回调被 try/catch 吸收 |
| 关停中 / 安全模式阶梯或重启服务或 PromptApplyRestart 的 Task.Run 在飞 | ❌ **未定义** | 这些 Task.Run（`Program.cs:1556,1716,2047`）不检查 `_shutdownInitiated`、无共享 CancellationToken——F14 |
| Tray 模式 + `_trayExitRequested=false` + 系统关机（CloseReason.WindowsShutDown） | ❌ **未定义** | 拦截分支不看 CloseReason——F15 |
| `SafeMode.IsActive && 正常模式清理` | ✅ 排除 | `Program.cs:365`（`!SafeMode.IsActive` 守卫） |
| `_updateRollbackArmedVersion != null && BootMonitor == null` | ⚠️ 安全降级 | 回滚不触发，退化为普通恢复流程（可接受） |
| `_shutdownInitiated && BootMonitor` | ✅ 排除 | `BeginShutdownAsync` 先 `BootMonitor?.Stop()`（`Program.cs:2472`），Stop 后所有 Report 幂等忽略 |

## 2.3 转换集中性核查（检查项 2）

`LauncherLifecycle` 只在 `LauncherApp.RunStartupAsync` 内被驱动；`app` 实例是 `RunLauncherAppPipelineAsync` 的局部量（`Program.cs:897-898`），启动完成后**无任何代码再持有它**。实际触发路径图：

```
启动段（✅ 汇入状态机）：
  SplashForm.OnShown → RunLauncherAppPipelineAsync → app.RunStartupAsync
    → Fire(StartRequested/InstanceConfirmed/RuntimeResolved|RuntimeFailed/
           ServiceStarted/ServiceReady|ReadinessTimedOut/UIInitialized|Fatal)

运行期（❌ 全部旁路状态机）：
  托盘菜单"退出"     → WindowManager.TrayExitAction → MarkTrayExitRequested + BeginShutdownAsync   （Program.cs:424-430）
  主窗关闭按钮       → FormClosing → (Tray 拦截 | 构建确认 | BeginShutdownAsync)                    （Program.cs:451-510）
  BootHealthMonitor.Failed → HandleBootHealthFailed → 安全模式阶梯 Task.Run / 重启服务 Task.Run     （Program.cs:1301-1362）
  更新完成回调       → PromptApplyRestart Task.Run / DownloadDshUpdateStaged Task.Run               （Program.cs:2047,2100）
  WebView2 ProcessFailed → WebViewManager 内部计数+自愈，从不调 app.HandleWebViewCrashed           （WebViewManager.cs:221-266）
  系统会话事件       → 无任何处理（无 SessionEnding/WM_QUERYENDSESSION/WM_POWERBROADCAST 订阅）
```

**旁路实锤（grep 证据）**：`ShutdownRequested` 在 src 中仅出现于枚举定义与转移表（`LauncherLifecycle.cs:29,54,67,74`），零 `Fire` 调用；`HandleWebViewCrashed` 仅定义（`LauncherApp.cs:325`）零调用。即 `Running→ShuttingDown` 这条最核心的转移**在生产代码中不可达**，退出走的是 `BeginShutdownAsync`（状态机外的静态编排）。`(Running, WebViewCrashed)→Running` 是死转移。

## 2.4 非法转换处理（检查项 3）

- `LauncherLifecycle.Fire`：未知组合抛 `InvalidOperationException`（Fail-Fast，`LauncherLifecycle.cs:90-92`），且有测试锁定（`LauncherLifecycleTests.IllegalTransition_Throws_AndStateStays`）。✅ 合规。
- `Fire(Fatal)`：非终结态→Failed，终结态幂等忽略（有注释定义）。✅
- 运行期无状态机，故无"default 分支吞非法转移"的问题形态；风险以"未定义交错"形式存在（见 F14/F15），而非静默扭转状态。无 P1。

## 2.5 竞态专项核查（检查项 4）

1. **进程 Exited 回调线程 vs UI 线程**：`BootHealthMonitor` 内部以 `_sync` 锁序列化状态转移，`Failed` 事件在锁外触发一次；`HandleBootHealthFailed` 的弹窗经 `form.BeginInvoke` 封送 UI 线程（`Program.cs:1343-1346,1352-1353`），落盘/计数在后台线程。✅ 定义良好。例外：`SafeModeState.Save` 无锁，`RecordFailure` 与 `RegisterBootFailure` 可并发 → F18（P3）。
2. **"停止中"收到启动请求**：安全模式/回滚/重启询问路径内部是顺序的（Stop→Start 同一 Task）；但与关窗并发未定义 → F14。
3. **"更新中"用户点退出**：构建中 → FormClosing 拦截 + 确认框（`Program.cs:477-503`）✅；Splash 应用更新中 → 取消按钮禁用（`SplashForm.Message.IsApplyingUpdate`，SplashForm 无关闭钮）✅。均已定义。
4. **关窗与模式切换同时发生**：`ReadLifetimeMode` 在 FormClosing 时现读磁盘，切换即生效语义一致；托盘图标补建失败 fail-open 放行关闭（`Program.cs:462-475`）。✅ 已定义。
5. **多次失败裁决并发重启**：`AskRestartDshServiceAfterBootFailure` 无会话闸门（对比 `AskEnterSafeModeOnce` 有 `TryConsumeSessionPrompt`），用户连续确认两次可产生两个并发 Stop/Start Task（概率低）。P3 观察。

## 2.6 状态转换日志（检查项 5）

`LauncherApp.cs:101-105`：`Logger.Info($"lifecycle: {s}")` ——只记**新状态**，无旧状态、无触发源（时间戳由 Logger 补）。对照铁律期望"旧→新→触发源→时间戳"，缺两要素。`BootHealthMonitor` 侧的 trace（attach/suspend/resume/failed）弥补了监控域，生命周期域缺失 → F17（P3）。

## 2.7 恢复路径（检查项 6）

崩溃/强杀后重启的收敛面核查（结论：**均有定义的收敛路径，质量良好**）：

| 不一致场景 | 收敛机制 | 证据 |
|---|---|---|
| 上次异常退出、服务残留监听 + pid 文件在 | `TryAdoptOrphanService`：PID 账本匹配 + HTTP 健康校验后接管（"接管即负责"） | `ServiceLifecycleOps.cs:37-61`；测试 `OrphanServiceOutcomes` |
| 服务残留但 pid 文件缺失/损坏 | 端口三态：Healthy→跳过拉起；Zombie→杀树重启 | `ServiceManager.ProbePort`；`LauncherAppScenarioTests.ZombiePort_*` |
| pid 文件指向已死/PID 复用进程 | 死→清文件；活着但不监听目标端口→**不杀**只清文件（防复用误杀） | `ServiceLifecycleOps.cs:71-89`（P1-3 注释） |
| pending-update.json 半写/损坏 | 原子写 + 读侧 catch→视为无 pending + Warn | `StagedUpdate.cs:86-118` |
| safe-mode.json 损坏 | catch→按未激活处理重建 | `SafeModeState.cs:101-105` |
| window-state.json 损坏 | catch→Warn（P1-4 治理：损坏不静默）+ 默认位置 | `WindowStateStore.cs:40-47` |
| runtimes 目标半成品 | IsSourceRuntimeComplete 门禁 + ReplaceStale 备份挪走 | `StagedUpdate.cs:221-286`；测试 `Regression_HalfBuiltSource_RefusedNotMoved` |
| 更新后数据被新版本迁移污染 | update-guard 首拍回滚 + 运行时隔离 | `UpdateDataGuard`；测试 ×6 |
| **就绪前壳死亡（服务已拉起未就绪）** | ⚠️ **不收敛**：pid 未记录（RecordServicePid 在就绪后）→ 服务健康无主，FollowWindow 下次关窗不停它 | F19（P3） |

## 2.8 可测试性（检查项 7）

- `LauncherLifecycle`：纯内存、零依赖，`LauncherLifecycleTests` 9 用例 ✅。
- `LauncherApp`：五 Manager 全接口注入 + 副作用委托可空，`LauncherAppScenarioTests` 10 用例（含真实文件副作用的 Stage0 用例）✅。
- `BootHealthMonitor`：探针/句柄/时钟间隔全注入，Headless 用例齐 ✅。
- **缺口**：运行期会话编排（`BeginShutdownAsync`、安全模式阶梯、`PromptApplyRestart`、构建状态）**没有可独立实例化的状态载体**——它们是 `Program` 静态方法 + 静态字段 + Form 的直接耦合，无法脱离 UI/Process 驱动。缺的抽象 = 一个"会话状态机"（Idle/Building/Restarting/ShutdownPending + 服务归属枚举）。这是 F13 的修复形态，也是维度一（可测试性）的预留接口。

## 2.9 维度二发现清单

| ID | 严重级 | 位置 | 一句话问题 |
|---|---|---|---|
| F13 | P2 | `Program.cs:44-2448`；`LauncherLifecycle.cs:54,67`；`LauncherApp.cs:325` | 运行期生命周期无状态机：`ShutdownRequested`/`WebViewCrashed` 零触发，会话状态=7+ 静态字段组合——直接违反铁律"状态机唯一真相源、严禁全局 static bool" |
| F14 | P2 | `Program.cs:1556,1716,2047` vs `Program.cs:2457-2515` | 关窗退出与安全模式阶梯/重启服务/更新重拉三组后台 Task 无共享取消或闸门，交错未定义：退出后仍可能拉起新服务/中途杀 npm（自愈靠下次启动接管） |
| F15 | P2 | `Program.cs:451-510`（全仓零 CloseReason 引用） | 系统关机/注销路径未定义：Tray 拦截分支不看 `CloseReason.WindowsShutDown`，关机时窗口被拦截隐藏、应用不退出（待人工确认：OS 弹"阻止关机"或超时强杀；强杀不走任何清理） |
| F16 | P2 | `WebViewManager.cs:309-321` | 插件崩溃触发源保真度：对整条 WebMessage JSON 做 Contains("ModuleLoader") 大小写不敏感匹配——前端任意提及该词的普通消息即触发 E1008+安全模式询问；且 `LastPluginCrashUtc` 会话内不复位，一次误匹配使本会话后续所有失败裁决都路由向安全模式（`Program.cs:1529`） |
| F17 | P3 | `LauncherApp.cs:101-105` | 转移日志只记新状态，缺 旧状态→触发源（对照铁律期望四要素缺二） |
| F18 | P3 | `SafeModeState.cs:108-138` | `Save()` 无锁：RecordFailure 与 RegisterBootFailure 可来自不同后台线程并发写，固定 .tmp 路径互踩，lastFailure/计数可能互相覆盖 |
| F19 | P3 | `Program.cs:334-337`；`ServiceLifecycleOps.cs:17-29` | PID 账本在就绪后才记录：就绪前壳崩溃 → 监听中的服务永久无主（Healthy 跳过拉起但不接管、FollowWindow 关窗不停它） |
| F20 | P3 | `Program.cs:1047` | `_cachedGlobalDshVersion = "unset"` 魔法哨兵表达"未缓存"（应 nullable+已读标记） |

**F13 最小修复建议（供后续统一整改参考，本批次不动代码）**：① `BeginShutdownAsync` 首行 `Fire(ShutdownRequested)`（需要把 app 实例存活期延长到 Running 之后——存 `Program.SessionApp` 静量即可）；② WebViewManager.ProcessFailed 主窗分支接 `SessionApp?.HandleWebViewCrashed()`；③ 新增 `ServiceOwnership` 枚举（External/ShellStarted/Adopted/Unmanaged）替换 `_serviceStartedByShell`+`_servicePid` 组合，`ShouldStopServiceOnClose` 改收枚举。风险：低（不改时序，只补记录与判定来源）；验证：Headless 场景测试断言退出轨迹含 `Running→ShuttingDown`。

**F14 最小修复**：静态 `CancellationTokenSource SessionCts`：三组 Task.Run 的循环条件与 npm 调用（已支持 ct）传入它；`StopShellService/StartDshServiceViaIdentity` 前检查 token；`BeginShutdownAsync` 触发 Cancel。验证：RealOS 测试——关窗瞬间处于安全模式重启窗口，断言退出后无新 node 进程。

**F15 最小修复**：FormClosing 顶部增加 `if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.SessionEnding)` 分支：Tray 模式**不拦截**，直接走 BeginShutdownAsync（服务停止比托盘隐藏更符合关机预期）。验证：手动注销/关机演练 + 日志断言 StopShellService 执行（OS 级行为暂无自动化通道，标注"待人工确认"）。

---

# §3 批次三：维度五（稳定性风险专项）

## 3.1 执行摘要

按"场景 → 代码处理 → 测试覆盖"逐项核查：**进程终止链（kill 不响应/PID 复用/防误杀）与更新链（半成品门禁/幂等应用/数据回滚）是全仓最强壮的部分**，均有 RealOS 回归测试锁定（符合 TESTING-GUARDRAILS 的 Bug 驱动复现铁律）。风险集中在三处：① 双实例并发启动（E1009）**零测试**（F21）；② 系统关机/注销清理未处理（= F15）；③ 更新应用在"Move 成功→ClearPending"之间崩溃的窗口（F23）。静默失败普查：全仓 ~212 处 catch，绝大多数带注释 best-effort 且下游有响亮错误码；**未发现 P1 级"应感知而全无感知"**，三处最弱点见 F24。

## 3.2 稳定性场景 × 覆盖矩阵

图例：代码 ✅=有显式处理 / ⚠️=部分；测试 ✅/⚠️/❌=有/部分/无。

### 进程类

| 场景 | 代码处理 | 测试 |
|---|---|---|
| 启动失败（找不到 node） | ✅ RuntimeResolver E1002/E1003/E1004/E1005 + 确认框下载便携版 | ✅ `LauncherAppScenarioTests.RuntimeFailure_E1004`；E1002/E1003/E1005 路径 ⚠️ 未逐一锁定 |
| 启动失败（node 在但 npm-cli.js 缺失） | ✅ E4001 明确报"已找到 Node 但未找到 npm-cli.js" | ⚠️ 未锁定 |
| 服务拉起失败（Start 抛/返回 null） | ✅ E2001 响亮 + 状态机 Fatal | ✅ 场景测试 |
| 启动后秒退 / 退出码非零 | ✅ BootHealthMonitor 进程层 E2007（exit 0 降级 Warn 防误报）+ 就绪失败清理 | ✅ `BootHealthMonitorTests`（Fake 句柄）+ `BootCrashOutcomes` |
| kill 不响应 | ✅ KillServiceProcess 三阶段（温和→强杀→重试强杀）+ 每步等 taskkill 自身退出 + E2005 + pid 文件保留下次清扫 | ✅✅ RealOS ×4（`Regression_BootLifecycle.RealOs.cs:67-160`：活监听/错端口拒绝/死 pid/非 node 拒绝） |
| 双实例并发启动 | ✅ 按端口 mutex + 找窗 5s + E1009（不再静默消失） | ❌ **零测试**（F21） |
| 端口被占用 | ✅ Foreign 三态快速失败 E2004，不傻等不误杀 | ✅ 场景 + `OrphanServiceOutcomes.ForeignPort` |
| 僵尸服务（TCP 开 HTTP 死） | ✅ Zombie→KillZombieTree（含祖先链）→重启；清理失败快速失败 | ✅ `ZombiePort_*` ×2 + RealOS |
| ⚠️ 反向风险：把用户自己的 node 当僵尸 | ❌ 身份判定仅进程名（维度三 F4） | ❌ |

### 更新类

| 场景 | 代码处理 | 测试 |
|---|---|---|
| 下载中断（npm pack 超时/取消） | ✅ ct/超时 Kill 进程树；tarball 存在性校验；失败终态文案 + E4001 | ⚠️ 构建失败路径有（`UpdateOutcomes.Regression_SilentValidationFailure`）；pack 中途 kill 无专门用例 |
| node 便携包哈希校验失败 | ✅ SHASUMS256 官方源优先双源校验，失败删除并 E1004 | ⚠️ 校验函数未单独锁定（待确认） |
| dsh tarball 完整性 | ❌ 无校验（F25，观察项） | ❌ |
| 目标文件被占用（runtimes 目标已存在/锁定） | ✅ AlreadyApplied 幂等 / ReplaceStale 备份挪走 / 挪走失败抛 E4002 透明 | ✅ `Regression_ApplyTargetExists_StaleMovedAside`、`Regression_ApplyTargetAlreadyValid_NoMoveNoBackup` |
| 更新中断电/强杀的中间残留 | ✅✅ 四道门禁：半成品绝不搬运（IsSourceRuntimeComplete）、清场再构建、pending 原子写、stale pending 指向 buildDir 时清除 | ✅ `Regression_HalfBuiltSource_RefusedNotMoved`、`Regression_NewBuildClearsStalePendingForSameBuildDir` |
| Move 成功→ClearPending 之间崩溃 | ⚠️ 收敛但绕路：下次启动按"旧版 pending"落 npm install 重装同版本（浪费 1-2 分钟） | ❌（F23） |
| 更新后首次启动失败 | ✅✅ update-guard：首拍→好符号确认→失败自动回滚数据+隔离运行时（E4003） | ✅✅ `UpdateDataGuardOutcomes` ×6 + `BootCrashOutcomes` |
| npm 成功但身份未变（FP1） | ✅ LogPostApplyIdentity 重发现取证 + Outcome 测试 | ✅ `Outcome_Update_NpmSuccess_WithoutIdentityChange_IsFalsePositive` |
| 更新失败 pending 死循环 | ✅ 重试类保留/非重试类清 pending + 降噪（MaxNotifyFailures） | ✅ `Update_Failure_*` ×3 |

### 文件类

| 场景 | 代码处理 | 测试 |
|---|---|---|
| 配置半写损坏 | ✅ pending/safe-mode/window-state/guard-manifest/safe-profile 均原子写；读侧损坏全部有 Warn 降级 | ✅ 各读侧容错随 Outcome 覆盖 |
| 例外：两处裸写 | ⚠️ `AppEnvironment.PurgeServiceLifetime`（settings.json，:174）、`RecordServicePid`（:23）非原子（F22） | ❌ |
| 并发写日志 | ✅ 统一日志 FileShare.ReadWrite + 写锁 + 有界重试（×10, 20ms）；Logger 主文件锁死回退 %TEMP% | ✅ `Regression_ServiceOutputPipe.RealOs`（双流落盘）+ `LoggerTests` |
| 磁盘满 | ✅ 各写侧 best-effort catch + 快照失败 E4003 响亮 + 日志 fallback；无专门测试（可接受） | ⚠️ |

### 系统类

| 场景 | 代码处理 | 测试 |
|---|---|---|
| 会话注销/关机清理 | ❌ 零处理（= F15：无 WM_QUERYENDSESSION/SessionEnding/POWERBROADCAST；Tray 拦截不区分 CloseReason） | ❌ |
| WebView2 缺失 | ✅ 注册表探测 + Bootstrapper 静默安装（120s 超时/杀树）+ E1006 分级留痕 | ⚠️ 无（网络+MSI 安装，可接受不测） |
| 无管理员权限路径 | ✅ 全部落用户级（DSH_HOME/%LOCALAPPDATA%/HKCU），HKLM 仅读 | ✅（隐含于沙盒 Outcome 测试） |
| 单实例数据目录锁（WebView2 0x800700B7） | ✅ E1006 专项文案 + form.Close | ⚠️ |

## 3.3 静默失败专项普查

全仓 src（不含 obj）约 **212 处** catch 引用、59 处 `catch {`/`catch` 起始块。逐类判定：

**合规示范**（预期内操作失败 + Warn + 明确路径）：`BootHealthMonitor` 全部 catch（attach 失败仅 Warn 不判死）、`UpdateDataGuard`（失败 E4003 响亮）、`StagedUpdate.ReadPending`（损坏 Warn + 当无 pending）、`WindowStateStore.Load`（损坏 Warn）、`DshUpdateManager` 全部失败路径（错误码 + UI 收口）。

**点名三处（P3，均有下游兜底但根因不可见/后果滞后）**：

| 位置 | 现状 | 用户是否应感知 | 判定 |
|---|---|---|---|
| `Domain/DshDiscovery.cs:170,220,317,333,374`、`Domain/JsEntryResolver.cs:37,58,104` | 裸 `catch { }`：package.json 损坏/路径异常静默吞 | 否（有下游 E2001），但根因（"package.json 解析失败"）不出现在任何日志 | P3：catch 内加一行 `Logger.Warn` 即可让 E2001 可归因 |
| `Managers/ServiceManager.cs:359-362`（PipeServiceOutputToUnifiedLog 最终放弃） | 重试 10 次后静默丢行 | 丢失的恰是服务崩溃堆栈——插件归因（`LogEvidenceIndicatesPlugin`）的证据源 | P3：最终放弃时 `Logger.Warn("dropped service log line: ...")`，避免安全模式路由证据悄悄丢失 |
| `Managers/ServiceLifecycleOps.cs:25-28`（RecordServicePid） | 裸 catch：pid 记录失败静默 | 症状滞后（下次启动无法接管残留服务） | P3：加 Warn；顺手改原子写（F22） |

**结论**：无 P1 级静默失败。"catch 后仅返回默认值"的位置（`IsHttpReady`/`PortOpen`/`GetProcessIdByPort` 等）全部是**探测语义的正常 false 路径**且有 ADR 注释，不属静默失败。

## 3.4 维度五发现清单

| ID | 严重级 | 位置 | 一句话问题 |
|---|---|---|---|
| F21 | P2 | `Program.cs:277-305`（tests 中 E1009/Mutex 零命中） | 双实例并发启动（mutex+E1009+找窗聚焦）零测试；且 second-instance 按 `FindWindowEx(null,null,null,"DeepSeek Harness")` 找窗——`--ui-probe` 探针窗口同名（Program.cs:743），探针运行期的第二实例会聚焦到探针窗口（边缘场景） |
| F22 | P3 | `AppEnvironment.cs:174`；`ServiceLifecycleOps.cs:23` | 两处裸 `File.WriteAllText` 写壳核心状态文件（settings.json purge / service-pid），与"核心状态文件必须 AtomicWrite"铁律擦边；读侧均有容错故降为 P3 |
| F23 | P3 | `DshUpdateManager.cs:278-292` | `Directory.Move(runtimeDir, targetDir)` 成功与 `ClearPending()` 之间崩溃 → 下次启动 pending.runtimeDir 失效 → 落 npm install 路径对同版本重装（行为收敛但浪费一次 1-2 分钟网络安装；无测试） |
| F24 | P3 | 见 §3.3 表 | 静默失败三处点名（discovery 裸 catch / 服务日志丢行无 Warn / RecordServicePid 静默）——应补 Warn 提升可归因性 |
| F25 | P3 | `Program.cs:2100-2164`（对照 `RuntimeResolver.cs:254-289`） | dsh tarball 下载（npm pack）无任何完整性校验，与 node zip 的 SHASUMS256 双源校验形成对比；本地 tarball 安装 npm 亦不校验 registry integrity——供应链面观察项 |

**测试缺口汇总（维度五）**：① E1009 双实例场景（可 Headless：mutex 名含端口可静态断言 + 跨进程集成测试）；② 系统关机清理（OS 级，标注"待人工确认"，可先补 CloseReason 分支的纯函数决策测试）；③ Apply 中断于 Move/ClearPending 之间（RealOS：构造 pending 指向已搬走的目录，断言行为收敛且不再重复 npm 安装——修复 F23 时补）；④ `VerifySha256Async` 校验函数（失败/官方源优先）单测。

**值得保留的强项**（防回归清单的正面确认）：KillServiceProcess 的 RealOS 四连测、更新链 Outcome 十连测、update-guard 回滚六连测、BootFailureRouting 三连测——这批"Bug 驱动复现"测试是本仓库符合 TESTING-GUARDRAILS 的直接证据，后续整改严禁削弱。

---

# §4 批次四：维度一（可测试性架构）

## 4.1 执行摘要

本仓库已确立**两条成熟的替代范式**：① 探针/副作用委托注入（`ServiceManager` 七个探针委托、`BootHealthMonitor` 全注入、`LauncherApp` 五 Manager 接口 + 副作用委托可空）；② 真实 OS 有界验证替代 Mock（RealOS 测试哲学 + 环境重定向钩子 `DSH_HOME`/`DSH_SERVICE_CMD` 等 30 个）。纯决策逻辑普遍已抽出为 ShellLogic 纯函数并配契约测试。**接缝缺口集中在"时间"与"静态初始化"**：`PollReadiness` 的等待完全不可注入（硬编码 Thread.Sleep + 墙钟宽限），导致它至今**零直接测试**（F26，P2）；`Program` 的 `static readonly SafeMode` 构造即读磁盘，测试无法重定向（F27，P3）。业务代码零 `Random`（无抖动需求）、网络执行无统一工厂但探针/执行双轨各有替代。改造风险整体可控，两处"构造即副作用"点已标注验证步骤。

## 4.2 硬依赖点普查（检查项 1）

> 每行回答一个问题：写测试时它如何被替代？无替代 = 接缝缺失。

### 直接 new Process / Process.Start（10 个真实调用点）

| 位置 | 用途 | 测试替代途径 | 接缝判定 |
|---|---|---|---|
| `Managers/ProcessRunner.cs:122,219` | npm/pnpm 执行漏斗（RunProcessCaptured/RunPnpmInstall） | RealOS 真进程（`RealWorldNpmExecutionTests`）；ct/超时参数化 | ✅ 充足（执行原语漏斗设计） |
| `Domain/DshDiscovery.cs:279` | `node entry --version` 版本探测 | internal `ProbeVersionOutput(fileName,args,timeout)` 参数注入 + RealOS（`DshDiscoveryProbeTests`） | ✅ |
| `Managers/ServiceManager.cs:224,271` | 拉起 dsh 服务 | `DSH_SERVICE_CMD` 注入假命令 + `IServiceManager` Fake | ✅ |
| `ShellLogic.cs:1352,1538` | taskkill 强杀 | RealOS 回归 ×4（Regression_BootLifecycle.RealOs） | ✅ |
| `RuntimeResolver.cs:157` | `node --version` 可用性探测 | 版本判定抽出纯函数 `IsUsableNodeVersion`（进程壳留 RealOS） | ✅（好示范：判定与进程分离） |
| `DiagnoseExport.cs:269` | 诊断采集（npm/dotnet --version） | `DiagnoseExportTests`（采集项可跳过） | ✅ |
| `Windows/LegacyUpgradeCleanup.cs`、`WebViewManager.cs`×3、`WebRuntimeInstaller.cs` | msiexec / 打开外部链接 / WebView2 安装器 | 无 | ⚠️ 可接受（低风险 UI 增值/一次性安装路径，标注即可） |

### File.* / Directory.*（约 400 处：root≈303 / Managers≈52 / Domain≈39）

替代范式 = **真实临时目录 + 路径注入**（非 mock FS）：
- ✅ 有注入点：`StagedUpdate.Init(dataDir)`、`UpdateDataGuard.Init(dataDir,dshHome)`、`WindowStateStore.Init(dataDir)`、`SafeModeState(storePath)` 构造注入、`SafeProfileBuilder(dshHome)` 构造注入、Logger.Init(path)、`DshDiscovery` 经 `DSH_HOME` 重定向。
- ⚠️ 接缝缺失：`Program.DshHomeDir/DataDir/UnifiedLogPath` 静态属性直读 env——但副作用已下沉 Manager，组合根自身无测试需求（可接受）；`Program.SafeMode` 为 static readonly **类型初始化即 Load 磁盘**（见 F27）。

### new HttpClient（9 处）

| 位置 | 替代途径 | 判定 |
|---|---|---|
| `ServiceManager`×3（探针） | `_httpProbe`/`_tcpProbe*` 构造委托注入 | ✅ |
| `BootHealthMonitor`（探针） | `_httpProbe` 注入 | ✅ |
| `UpdateChecker.FetchLatest*` | HttpClient 参数传入（测试用假 Handler） | ✅ |
| `DshUpdateManager.EnsureDshInstalled:76` | **无注入点**（内部 new + FetchLatestDshVersionAsync）——预算/换源逻辑靠 `ProvisionPolicy` 纯函数 + RealOS 兜底 | ⚠️ F28 |
| `ServiceLifecycleOps.IsReady`、`RuntimeResolver`×2、`WebRuntimeInstaller` | 无（执行型网络，RealOS/可接受） | ⚠️ |

结论：网络接缝"双轨"——**探针型已注入、执行型靠 RealOS**，形态自洽；唯一值得补的是 EnsureDshInstalled 的版本解析步（F28）。

### 注册表 / 环境变量

- env 读取 45 处、30 个变量：**测试钩子本身就是接缝设计**（`DSH_TEST_*` 系列 10 个 + `DSH_NO_UI`/`DSH_E2E`/`DSH_SANDBOX`/`DSH_SERVICE_CMD` 等）；清单已登记至 `docs/TEST-LAYERING-POLICY.md` 附录。缺口：钩子无命名规范约束（生产/测试混杂）→ F30（P3）。
- 注册表 7 文件：`ReadWebView2Version`/`FindViaRegistry`/`EnsureAutoStartRequested`(HKLM 只读) 无参数化 hive 注入——低风险环境探查，标注 → F29（P3）。

### DateTime / TickCount / Sleep / Random

- `DateTime.UtcNow` 40 处：**好的范式已在**——纯函数收时间参数（`UpdateGuardPolicy.SnapshotDirName(version, utc)`、`Logger.ShouldRotate(length, lastWrite, nowUtc)`、`ResolveProfile`），RealOS 有界等待兜底。
- `DateTime.Now` 9 处：全部用于日志/pending 时间戳**展示**，无逻辑依赖 ✅（`UpdateDataGuard` 备份文件名 2 处亦展示用途）。
- `Thread.Sleep` 8 处 + `Task.Delay` 12 处：详见 §4.3 时间专项。
- `Environment.TickCount64` 3 处：WebViewManager 崩溃节流 / UpdateBuildStatus 合流节流（UI 域，10s/150ms 窗口硬编码）——可接受。
- `Random`：**业务代码零使用**（仅生成代码）✅——重试无抖动需求，无抽象必要。

## 4.3 时间抽象专项（检查项 3）：等待能否压缩？

| 等待逻辑 | 位置 | 可注入？ | 测试压缩到 30ms？ |
|---|---|---|---|
| `WaitReadyAsync(port, timeout, ct)` | `ServiceManager.cs:51-63` | ✅ timeout+pollDelay 均参数 | ✅（`ServiceManagerTests.WaitReady_*` 即以毫秒级参数运行） |
| **`PollReadiness`** | `ServiceManager.cs:70-132` | ❌ `Thread.Sleep(i<8?200:1000)` + 5s 日志检查间隔 + 15s 宽限 + e2eMode 只缩预算（20 轮）不缩延迟 | ❌ **最短 headless 路径 >8s → 至今零直接测试**（F26） |
| `BootHealthMonitor` 全部轮询 | ctor 注入 `logPollInterval`/`httpPollInterval` + `BootProfile` 的 GraceMs/ProbeIntervalMs | ✅ | ✅（BootHealthMonitorTests 以 1ms 间隔驱动全矩阵） |
| `WaitForProcessExit(pid, timeout)` | `ShellLogic.cs:1569-1578` | ❌ 50ms 真实轮询（有界 ≤1.5s） | ⚠️ 可容忍（进程死亡检测本需 RealOS） |
| `StopService` 端口释放等待 | `ServiceLifecycleOps.cs:155-157` | ❌ 1s deadline + 80ms sleep 硬编码 | ⚠️ 有界，可容忍 |
| `KillServiceProcess` 三阶段 | `ShellLogic.cs:1517-1524` | ❌（4000/800/1500ms 常量） | ⚠️ RealOS 已覆盖语义；压缩无必要 |

**结论**：net10.0 可用 `TimeProvider`——**新代码**的等待一律经 `TimeProvider`/延迟委托；**存量**仅在"接下来要修"的路径提取（F26 与 F2 同函数，修复时顺带注入 delay 委托，一石二鸟）。

## 4.4 改造风险评估（检查项 4）

| 候选改造 | 时序/语义风险 | 验证步骤 |
|---|---|---|
| `PollReadiness` 注入 delay/时钟 | 轮询顺序（日志检查先于 TCP 探测）与宽限语义必须逐位保留——否则 F2 修复引入新回归 | 修复 PR 附 RealOS：陈旧标志不判死 + 新增标志 15s 后判死 双用例 |
| `Program.SafeMode` 懒加载化 | **有行为变化风险**：启动早期 Fail 路径（`HandleBootHealthFailed`）依赖"类型初始化即已 Load 的 IsActive/Tier"；改懒加载后早于首次访问的判断会变 | 若动它：Headless 场景断言"粘滞安全模式会话"首问路径行为不变；否则**不改**（P3 保留现状） |
| app 实例延寿（F13 修复，存 `SessionApp`） | 无启动时序变化（Fire 点不变），仅延长持有 | Headless 断言退出轨迹含 Running→ShuttingDown |
| EnsureDshInstalled 注入版本解析 | 构造函数无副作用 ✅，注入仅增参数，无时序变化 | RealOS 保留原路径覆盖 |

## 4.5 维度一发现清单

| ID | 严重级 | 位置 | 一句话问题 |
|---|---|---|---|
| F26 | P2 | `ServiceManager.cs:70-132` | `PollReadiness` 时间完全不可注入（硬编码 sleep/宽限/间隔），最短测试路径 >8s → **零直接测试**；与 F2 同函数，修复时一并注入 delay 委托/TimeProvider |
| F27 | P3 | `Program.cs:59-63` | `static readonly SafeMode = new SafeModeState(...)` 类型初始化即读磁盘（构造即副作用），测试无法重定向；改造有行为风险，标注验证步骤（倾向保留现状） |
| F28 | P3 | `DshUpdateManager.cs:76-77` | 首装链的 registry 版本解析无注入点（内部 new HttpClient + UpdateChecker 直调），只能 RealOS 整链覆盖 |
| F29 | P3 | `ShellLogic.RuntimeConfig.ReadWebView2Version` 等 3 处 | 注册表读取无 hive 参数化（低风险环境探查，标注即可） |
| F30 | P3 | 全仓 45 处 env 读取 | DSH_* 钩子无命名规范与统一清单（生产/测试混杂）——清单已补入 TEST-LAYERING-POLICY 附录，规范已写入 |

---

# §5 批次五：维度四（测试策略与碎片化评估）

## 5.1 执行摘要

测试体系**质量高于数量**：~450 用例、零 Moq、零反射进私有、命名全行为式（`方法_场景_期望`）、Theory 参数化广泛、RealOS 哲学贯彻（进程/锁/编码全部真机验证）。分层实测 **L1 纯逻辑 ≈55% / L2 组件 ≈33% / L3 RealOS ≈6% / L4 E2E ≈3%**——前三层健康，唯一结构性缺口是 **golden 契约样本 = 0**（F31，P2）：五个 dsh 输出解析器没有一个被真实输出片段锁定，dsh 单方面变更时无哨兵（与维度三 F2/F3/F6 形成同一主题的"防护缺位"）。碎测试信号抽查**基本为零**；建议的合并/删除仅针对死代码而非测试。配比目标与 golden 规程已固化为 `docs/TEST-LAYERING-POLICY.md`。

## 5.2 分层盘点（检查项 1）

| 层 | 实测占比 | 代表文件（用例数） | 判定 |
|---|---|---|---|
| L1 纯逻辑单元 | ≈55%（≈250） | ShellLogicTests(33)、BootGuardContractTests(30)、V030FeaturesTests(37)、UpdateCheckerTests(17)、UpdateFlowContractTests(14)、ContractTests(14)、UpdateProgressContractTests(13)、BootFailureRoutingContractTests(10)、LauncherLifecycleTests(8) 等 | ✅ "多而便宜"，参数化充分 |
| L2 组件集成 | ≈33%（≈150） | BootHealthMonitorTests(23)、DiagnoseExportTests(19)、ServiceManagerTests(11)、LauncherAppScenarioTests(9)、Outcomes/ 15 文件（≈57） | ✅ 每公共入口 3-5 场景达标 |
| L3 RealOS+契约 | ≈6%（≈25） | Regression_BootLifecycle.RealOs(4)、RealOsProcessTests(5)、DshDiscoveryProbeTests(4)、RealWorldNpmExecutionTests(3)、DshUpdatePipelineRealTests(2) 等 | ⚠️ RealOS 质量高；**golden 样本 0** |
| L4 E2E | ≈3%（14） | DshUpdateFlowTests(2)、UiTestHookE2ETests(2)、MaximizeAcrossVirtualDisplayTests(2)、StartupLatencyTests(1)、UiResponsivenessTests(1) + 探针辅助 | ✅ 符合"仅冒烟"（5±2 条级） |

## 5.3 碎测试信号抽查（检查项 2）

| 信号 | 检查结果 |
|---|---|
| 断言私有方法/内部状态 | **零**：全仓无 `BindingFlags.NonPublic`/`GetField`/`GetMethod` 反射；`InternalsVisibleTo` 只暴露 internal（服务级） |
| mock 后只断言"被调用过" | **零**：全仓无 Moq/Verify(Times.*)——手写 Fake + 物理副作用断言（Outcomes 范式） |
| 断言完整字符串相等 | 未发现长串全等；`BuildToastXml` 等断言结构片段 ✅ |
| 复制粘贴只差一值 | 未发现成片重复；Theory 使用广泛（`ResolveEffectiveLifetime_PluginPresent_NeverPurges(string?)` 等） |
| 测试名不描述行为 | **零**：全部 `方法_场景_期望` 三段式 |
| 组织性债（非碎测试） | `V030FeaturesTests`(37 例) 跨 lifetime/窗口/更新/错误码五域杂烩 → F32（P3，按域拆归位）；E2E `DshUpdateFlowTests` 与 `UpdateOutcomes` 场景重叠（双保险可接受，待人工确认是否保留） |

## 5.4 目标分层配比（团队约定 → 已固化 docs/TEST-LAYERING-POLICY.md）

- 状态机/纯逻辑：单元覆盖全部转换矩阵，允许"多而便宜"，优先参数化 —— **维持现状 50-60%**；
- Managers 组件：每公共入口 3-5 场景（正常/边界/失败），不追行覆盖率 —— **25-35%**；
- dsh 契约：**每个解析器配 golden 样本测试**（真实输出片段存 `tests/DshShell.Tests/GoldenFiles/dsh/`；样本失败 = dsh 变了，改样本即改契约并同步 `DSH_CONTRACT_INVENTORY.md`）—— **10-15%（与 RealOS 合计）**，当前最大缺口；
- E2E：仅 3-5 条冒烟（启动到就绪、切换驻留模式、退出清理）—— **≤5%**，现状 14 例略超但符合冒烟精神。

## 5.5 何时**不**写测试（检查项 4，已固化进 POLICY 文档）

一次性脚本 / 纯 UI 布局（用 `--ui-selftest` 代）/ 纯透传委托属性 / 生成代码 / 为覆盖率数字补测的简单属性。覆盖率只用于找"从未走过的异常路径"（本次审查的三处裸 catch 即此类证据），不当 KPI。

## 5.6 维度四发现清单

| ID | 严重级 | 位置 | 一句话问题 |
|---|---|---|---|
| F31 | P2 | `tests/DshShell.Tests/`（GoldenFiles 缺失） | dsh 输出解析器（StartupErrorMarkers/BootErrorMatchers/IsShellAuthoredLogEntry/ProbeVersionOutput/EvaluatePageProbe 输入）**零 golden 样本**——dsh 单方面变更无哨兵；修复维度三 F2/F3 时必须同步补 |
| F32 | P3 | `V030FeaturesTests.cs`(37 例)；E2E `DshUpdateFlowTests` vs `Outcomes/UpdateOutcomes` | 组织性债：跨五域杂烩文件建议按域拆归位；E2E 与 Outcome 层更新场景重叠（双保险，待人工确认取舍） |

---

# 全量汇总（F1–F32）

| 级别 | 数量 | ID |
|---|---|---|
| P1 | 2 | F1（双版本比较器漂移）、F2（就绪轮询吃全量历史日志） |
| P2 | 11 | F4、F5、F6、F7（维度三）；F13、F14、F15、F16（维度二）；F21（维度五）；F26（维度一）；F31（维度四） |
| P3 | 19 | F3、F8、F9、F10、F11、F12（维度三）；F17、F18、F19、F20（维度二）；F22、F23、F24、F25（维度五）；F27、F28、F29、F30（维度一）；F32（维度四） |
| P0 | 0 | 无阻断/崩溃类缺陷 |

**建议整改提交序列**（待指令后执行）：① F1（比较器统一，纯函数级）；② F2+F26（同函数：增量日志 + delay 注入 + golden 样本）；③ F13+F14+F15（会话状态机三连）；④ F4/F5/F6/F7/F16/F21/F31（防护面）；⑤ P3 清理批（F8/F9/F22/F24/F30 死代码与归因性 Warn）。每批独立可回滚。
