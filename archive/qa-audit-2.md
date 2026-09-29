# dsh-launcher 下一阶段质量审计报告（只读，2026-08）

> 本报告为**只读审计**：未修改任何产品代码、测试、脚本或文档。全部结论基于真实代码阅读与实测（`dotnet test` 实测通过 262/262，455ms），证据格式 `文件:行号`。
> 审计由 5 个并行子审计（测试资产 / 资源生命周期 / 进程树与状态机 / 契约·性能·诊断 / CI·发布·文档）合并而成，主控已逐条交叉验证。
> 本文件位于 `archive/`（git-ignored），不进入仓库，仅作人工审阅留存。

---

## 1. 执行摘要

- **项目当前最大的问题**：没有大问题，但有一个真实缺陷——**启动中途取消/退出会使已拉起的 dsh 服务变成永久无主孤儿**（Program.cs 的清理分支只覆盖 `logerror/timeout`，不覆盖 `canceled`；PID 文件只在 `ready` 时写入）。服务占着 3080，之后的会话永远无法接管它，除非端口恰好关闭。
- **项目当前最大的风险**：**静默崩溃无留痕**——全程序没有任何 `AppDomain.UnhandledException` / `Application.ThreadException` 钩子，UI 线程或后台线程一旦抛未捕获异常，进程无声消失，dsh.log 里什么都没有。这与"失败必须可诊断"的哲学直接冲突。
- **是否过度测试**：数量上不过度（262 个断言 455ms 全绿、零 flaky、全是确定性纯单元），但**存在 ~40% 的断言级重复和 3 个"永真假绿灯"测试**。病在冗余与假断言，不在数字。
- **是否存在臃肿倾向**：无。1.4MB MSI、无新依赖、无抽象层、脚本/文档均克制。个别卫生问题（`.wix/` 目录被 gitignore 却追踪了一个 0 引用的 DLL；`docs/v0.3.0-plan.md` 完成但版本口径含糊）属小瑕疵。
- **下一阶段最应该做**：① 修"取消启动→服务无主残留"；② 加崩溃留痕钩子；③ 测试去重删假绿灯（262→约 150-170）；④ 主题轮询 500ms 全量读文件改 mtime 缓存；⑤ 删 CI 双跑 dotnet test。
- **下一阶段最不应该做**：引入 Job Objects（与既有 pid 认领机制冲突、需重构启动链路）、加 coverage badge、CI 分层/缓存、负向/E2E 进日常 CI、为"看起来专业"做任何事。

---

## 2. 事实基础

主控亲自阅读（非仅子审计转述）：

- 全部源码：`src/DshShell/Program.cs`（3104 行）、`ShellLogic.cs`、`RuntimeResolver.cs`、`UpdateChecker.cs`、`StagedUpdate.cs`、`Logger.cs`、`DiagnoseExport.cs`、`ErrorCodes.cs`、`WindowStateStore.cs`
- 全部测试：`tests/DshShell.Tests/` 6 个文件；`scripts/test.ps1`、`negative-test.ps1`、`e2e-test.ps1`、`build-release.ps1`、`check-prereq.cmd`、`uninstall-autostart.cmd`、`start-dsh.vbs`
- 文档与 CI：`README.md`、`docs/README.en.md`、`docs/DETAILS.md`、`docs/testing-governance.md`、`CHANGELOG.md`、`.github/workflows/build.yml`、issue 模板
- 实测：`dotnet test -c Release` 262/262 通过（455ms）；MSI 1.36MB；真实 `~/.dsh/dsh-launcher/` 目录（dsh.log 406B、service-pid-3080.txt、settings.json=`serviceLifetime:2`、残留 shell.log 与 theme.json）；git 追踪状态（`.wix` 误追踪确认）；grep 验证（无 Job Object、无异常钩子、无 E1001/E3001 发射点）。

---

## 3. 测试资产审计

### 3.1 现状

- 262 断言 = 118 个 `[Fact]/[Theory]` 方法（99 Fact + 163 InlineData），实测 455ms 全绿、无重跑。
- 分类（单测层）：纯单元 ~52、文件系统 ~48、伪网络（FakeHttpMessageHandler）16、环境依赖 ~6、进程/UI/网络 0（由脚本层承担）。
- 脚本层：`test.ps1` 静态+隔离行为断言（27+）、`negative-test.ps1` 8 用例 20 断言、`e2e-test.ps1` 7 段 37 断言。
- 结构判断：**"快速纯单元层 + 少量关键集成层"已经成立且正确**，不需要推翻。

### 3.2 高价值（保留，约 200 个断言）

SecurityBoundary 可执行面全拒绝（41 行 InlineData，S2 核心）、PickOldInstalls/FilterByUpgradeCode（9 条，防误删注册表产品）、RestoreWindowPosition（7 条多显示器容灾）、ResolveEffectiveLifetime（8 条配置降级 P0 防线）、StagedUpdate（8 条延迟更新状态机）、DiagnoseExport Sanitize/TailLines/SummarizeErrors（v0.3.1 共享读回归）、UpdateChecker Fetch*（15 条伪网络）、Logger 级别/写失败静默、SplitLParam（B1 回归）等。维持策略：一函数一主题，不动。

### 3.3 低价值/可删除（假绿灯）

| 测试 | 证据 | 问题 |
|---|---|---|
| `NodeMissingReason_WithUsableNode_ReturnsNull` | V030FeaturesTests.cs:399-411 | else 分支空断言，唯一 if 只再断言一个关联值——**永不失败** |
| `NodeMissingReason_NoNodeAnywhere_ReturnsNotFound` | V030FeaturesTests.cs:377-396 | 注释自认"not-found 或 null 均可接受"，仅断言 `!= "too-old"`——**永不失败** |
| `CurrentLauncherVersion_FromAssembly_NonEmpty` | UpdateCheckerTests.cs:177 | 恒真，无回归意义 |
| SecurityBoundary 两个 `IsAutoGrantedPermission_*` | SecurityBoundaryTests.cs:70,80 | 被 ShellLogicTests 全枚举（12 行）完全包含的真子集 |

### 3.4 可合并（重复验证同一件事）

- `CompareVersions`：ShellLogicTests.cs:23 与 UpdateCheckerTests.cs:171 测**同一函数**，边界大量重叠 → 留一份。
- `ResolveTarget`：ShellLogicTests.cs:39 与 SecurityBoundaryTests.cs:105 大量相同用例 → 留一份。
- `IsSafeToOpen` 无害扩展名子集：ShellLogicTests.cs:182 与 SecurityBoundaryTests 重叠（pdf/png/txt/json/mp3/zip 重复出现）→ 保留 SecurityBoundary 41 行可执行面为主，无害类精简。
- `ShouldRotate`：V030FeaturesTests 与 LoggerTests 各两条测同一阈值 → 合并为一处 4 断言。
- 产品代码冗余：`ShellLogic.ReadLogTail`（ShellLogic.cs:304-324）与 `DiagnoseExport.TailLines`（DiagnoseExport.cs:107-128）是两个各自实现的近重复尾部读取 → 合并为单一实现后再测（减实现+减双测，符合做减法）。

### 3.5 治理结论

- 262 不过量但冗余：**删 4 个假绿灯 + 合并重复 → 目标 150-170 断言（约 -38%~-45%）**；不建议低于 130（会伤 P0 防线厚度）。
- 两个 NodeMissingReason 测试当前**根本没有被真正断言**——要么给 `RuntimeResolver` 加可注入候选辨析器后纯测，要么直接交给 negative 脚本层（已进程级覆盖）。
- 结构性 flaky 隐患（未发生）：`Logger._path`/`StagedUpdate._pendingPath`/`WindowStateStore._path` 是进程级静态单例，xunit 类间默认并行——当前各单例只有单一测试类触碰，安全；**新增测试类时必须避开并行触碰同一单例**，建议在测试文件头加注释或在 governance 文档记一条。
- CI 稳定性：本机单测 455ms 全绿；唯一环境依赖项（HasExecutableOnPath 的 `Z:\`、Sanitize 的真实用户名）已用"不存在文件名/宽松断言"修脚，风险可控。

---

## 4. 资源生命周期风险

| 风险 | 位置 | 后果 | 修复 | 优先级 |
|---|---|---|---|---|
| 共享 `CoreWebView2Environment` 从不 Dispose；主窗 WebView2 在 FormClosing 故意不 Dispose（注释权衡：Dispose 会等浏览器进程关、卡 1-2s） | Program.cs:245, 632-635 | 单次退出靠进程回收，无累积泄漏；退出路径无确定性释放 | **建议保持现状**（进程退出即回收，克制选择），人工确认 | P2（确认项） |
| 子进程 `Process` 对象未 using：wscript（P415）、taskkill×2（P1696/1704）、外部链接/新窗（P2291/2344） | 同上 | GC 兜底可回收，与全文件其余 `using var` 不一致 | 机械改写为 using | P2 |
| 更新下载中途关窗不可取消（`DownloadDshUpdateStaged` 无 CT） | Program.cs:1113 | 半成品不会误应用（MarkPending 仅成功时写）；staging 残包 7 天清理 | **建议保持设计**（后台跑完），人工确认 | P2 |
| 无 `PowerModeChanged`/`SessionEnded` 处理 | 全文件 | 休眠唤醒/注销时 WebView2 可能异常 | 可选：唤醒后 Reload；或记为已知边界 | P2 |
| 启动 poll 的 `http.GetAsync` 未传 CT（取消最坏延迟 3s） | Program.cs:467 | 非泄漏，取消不够即时 | 传 ct | P2 |

**已正确处理（勿动）**：Logger 每次 `AppendAllText`（无常驻句柄+全局 lock）、HttpClient 全部 using+超时、同步子进程全部 using+超时 Kill、单实例 Mutex using（含异常退出）、托盘 NotifyIcon 正常退出释放、`ReleaseThemeWatcher` 统一释放、TrayMenuForm Timer/字体/DC/GDI 全清理、staging/临时目录清理、杀进程前身份校验。

---

## 5. 进程树与崩溃恢复风险

启动链：`Main` →（端口未开）`SweepStaleServicePid` → `ApplyPendingDshUpdate` → Node 解析 → `wscript start-dsh.vbs` → `cmd /c dsh web --host 127.0.0.1 --port N`（或 npx 回退）→ `node`（最终监听 PID）。壳只管理最末端 node PID。

| 风险 | 路径 | 后果 | 最小围栏 | 优先级 |
|---|---|---|---|---|
| **启动中取消/退出 → 服务无主残留** | Program.cs:504-539：清理分支仅 `logerror or timeout`（P507），不含 `canceled`；PID 仅 `ready` 时写入（P542） | 取消后 node 继续占 3080 且无 pid 文件 → 之后所有会话都无法接管，**永久无主** | canceled 分支：若已监听则 `RecordServicePid`（下次可接管）；若仍在下载则保留（与取消文案一致）并记录 | **P0** |
| 强杀 DshWeb.exe | VBS 分离启动（`sh.Run(...,0,False)`） | 服务残留占端口 | 已有缓解：下次启动 SweepStale/TryAdopt 认领 | P1（已缓解） |
| PID 复用误杀无关 node | ShellLogic.cs:341-349 仅校验进程名==node | 误杀其他 node 应用 | 追加"确在监听目标端口"校验 | P1 |
| node 子进程树不随主进程杀 | Program.cs:1696,1704 的 taskkill 无 `/T` | dsh 若派生 worker 则残留 | **taskkill 加 `/T`（一行）** | P1 |
| 更新中途关闭 | 后台 Task 被进程终止 | 孤儿 npm/node；staging 残包可清理；无脏 pending | 低风险可接受 | P2 |

**Job Objects 结论：不引入。** 理由：① 壳经 wscript/cmd 分离启动，根本没有 node 的 Process 句柄，绑 Job 必须重构启动链路（`UseShellExecute=false` 直连）——违背"不改变服务启动链路/不接管 dsh 上游"的产品边界；② 与既有 SweepStale/TryAdopt 认领机制形成两套清理哲学；③ 现有缺口只需 `taskkill /T` 一行补齐。引入反而膨胀。

---

## 6. 状态机与竞态风险

**实现方式**：无显式状态枚举，靠流程顺序 + 3 个布尔（`_serviceStartedByShell`/`_trayExitRequested`/`_pendingUpdate`）+ 端口/HTTP 重探测。状态：初始化→环境检测→依赖检测→拉起中→等 ready→WebView2 初始化→就绪→关闭/托盘驻留→更新下载/待应用→退出清理。

| 竞态 | 证据 | 后果 | 修复 | 优先级 |
|---|---|---|---|---|
| 启动中取消（服务已拉起未就绪） | P504-539 | 服务无主残留（同 §5 第一条） | 见 §5 | **P0** |
| 第二实例 20s 找不到主窗直接 return | P344-361 | 首实例卡 WebView2 初始化时二次点击"没反应" | 边际；可选：超时后提示 | P2 |
| WebView2 初始化失败时 `form.Close()` 连带停服务 | P726-760 vs P643-647 | 初始化失败会把可留用的服务停掉 | 复核模式后决定；多数场景可接受 | P2 |
| 取消被归为 E9001"内部未分类" | P529-538 | 诊断误导（"取消"≠"内部错误"） | 独立码（如 E2006）或并入 E2002 语义 | P1（随 P0 一起） |
| 托盘退出路径二次 StopShellService | P1824-1829 → P643-647 | 无害（IsLikelyDshService 幂等兜底） | 无需 | — |
| 外部托管模式 pid 文件 | P1542-1557 | 已安全（RecordServicePid 仅在壳托管分支） | 无需 | — |

**死锁/卡死**：无真正的同步上下文死锁（UI 线程唯一 `.GetResult()` 是带嵌套消息循环的 Node 下载，安全；其余全部后台线程+BeginInvoke）。唯一卡顿是 `StopShellService`→`KillProcess` 在 FormClosing 同步阻塞 UI 最长 ~2s——限时、非死锁，但与"关窗不卡顿"的质量目标有张力，属可接受的取舍。

---

## 7. 上游契约风险

| 契约 | 破坏影响 | 脆弱度 | 验证现状 |
|---|---|---|---|
| **C3 ready 判定**（TCP+HTTP GET 成功=就绪） | dsh 改"监听即 200 但 UI 未就绪"→ 提前开窗白屏；"延迟 HTTP"→ 每次启动慢 | **最高** | **无自动验证**；逻辑内嵌 Program.cs:438-485，不可注入。最小修复：抽 `IsReady(http,port)` 纯函数 + HttpListener stub 契约测试 |
| **C9 运行时身份假设**（dsh 服务一定是 node 进程名） | dsh 换运行时（bun/原生）→ IsLikelyDshService 全拒 → 服务永不停止，**无报错**（进程泄漏） | 中高 | 无单测；**需人工调研 dsh 是否会换运行时** |
| C1 启动命令（`dsh web --host --port`；npx 回退） | 参数改名/包名变 → 冷启动失败 E2002/E2003 | 高 | 文档已声明为已知变动点；真实验证只能降频（tag 发布前 -Smoke 一次） |
| C10 Node ≥18 门槛硬编码壳侧 | dsh 若要求更高版本 → 壳判可用实际跑不起来，误导诊断 | 中 | 无单测（可 stub node.cmd 伪造 --version 输出测阈值） |
| C6/C7/C8 settings.json + 插件双所有权 | 插件改字段名 → 用户"常驻"失效（非插件缺失情形无提示） | 中低 | 已有精确键判定单测 ✓；插件侧字段已对齐 |
| C4/C5 日志契约（唯一所有权归壳、%TEMP% 回退） | vbs 回归截断/轮转 → 双写冲突 | 低 | 已有 R06 静态断言 ✓ |
| C11 更新接口（GitHub/npm/`-sec` 约定） | 字段变 → 静默不推（可接受）；标记漂移 → 平凡更新当安全更新推送 | 中低 | 已有 FakeHttpMessageHandler 单测 ✓ |

**契约测试边界**：契约测试=壳侧解析/判定纯函数+本地 fixture/stub（秒级、CI 必跑）；集成测试=真实进程/网络（降频、限时、可 SKIP）。当前缺口集中在 C3/C9/C10 三处**尚不可注入的判定**，先做"抽纯函数+stub 断言"是最小改动、最大契约保障。

---

## 8. 性能风险

**唯一值得动的热点：主题轮询每 500ms 全量读 settings.yaml**（Program.cs:3072-3075 → 2513 `File.ReadAllLines` + 注册表 2494），UI 线程同步执行，settings.yaml 可能较大。**最小修复：按 mtime 缓存（watcher 已存在，轮询只是兜底；间隔可提至 2s，或仅 mtime 变化才重读）**。优先级 P1。

其余全部"不需要优化"（有证据）：更新检查异步不阻塞启动、`ResolveLocalDshVersion` 后台线程、Node 探测仅残缺环境多 spawn（可接受）、`FindPidListeningOn` 关窗路径已用内存缓存、JSON 序列化非热路径、日志每次 AppendAllText 开合文件（频率低，无优化价值）、UI 线程阻塞全部受控限时。

---

## 9. 诊断能力风险

| 缺口 | 位置 | 后果 | 修复 | 优先级 |
|---|---|---|---|---|
| **无任何未处理异常钩子** | 全文件（grep 证实） | 崩溃静默退出、零留痕 | `Logger.Init` 后挂 `Application.ThreadException` + `AppDomain.UnhandledException`，写 `E9001` + 异常详情。**几行，只加诊断不加恢复逻辑** | **P0** |
| window-state/pending 损坏静默回退 | WindowStateStore.cs:37、StagedUpdate.cs:76 | 位置记忆/更新待应用状态失效无告警（settings.json 已有 Warn，此处没有） | 补一条 Warn | P1 |
| E1001 死码（目录/文档有定义、无发射点） | ErrorCodes.cs（无 E1001） | Node 缺失拿不到语义准确码 | 文档标注或删除 | P2 |
| E9001 被"取消启动"占用 | Program.cs:533-538 | 诊断误导 | 独立码（随 P0-1） | P1 |
| testing-governance §10 声称 E3001"定义无发射点" | 文档（实际 E3001 已在 CHANGELOG 0.3.0 删除） | 文档 vs 代码不一致 | 更正文档 | P2 |
| 真实环境残留 `shell.log`/`theme.json`（旧版二进制/插件写入，当前代码不读写） | 真实 `~/.dsh/dsh-launcher/` | 排障时误判"日志双写" | --diagnose 或文档一句话说明 | P2 |
| UpdateChecker 网络失败完全静默 | UpdateChecker.cs:41-44 等 | 用户无从知晓更新检测未跑 | 可选：Info 级日志一条 | P2 |

**已达标（勿动）**：日志分级+`DSH_LOG_LEVEL`、错误码贯穿弹窗/日志/诊断、轮转（30MB/>3 天保留≤3）+常驻超长告警、脱敏（Sanitize 四层）、共享读（v0.3.1 修复）、诊断 zip 路径落日志与 stdout、错误码汇总、用户找日志路径顺畅（错误弹窗带完整路径）。

---

## 10. CI 与发布风险

| 问题 | 证据 | 建议 | 优先级 |
|---|---|---|---|
| **dotnet test 在 CI 跑两次** | build.yml L24-32 独立步骤 + test.ps1 L33 内部又跑一次 | 删 build.yml 独立单测步骤，test.ps1 作唯一入口 | **P1** |
| `.wix/` 已 gitignore 却追踪一个 DLL | `git ls-files` 命中 `.wix/extensions/.../WixToolset.Util.wixext.dll`，全仓库 0 引用 | `git rm --cached` | P1 |
| 无 v0.3.0 tag（v0.2.5→v0.3.1 跳版） | `git tag` | CHANGELOG `[0.3.0]` 节补注"未单独发版、随 0.3.1 交付" | P2 |
| DETAILS.md 过时点 | L97 `-Version 0.1.8` 示例；scripts 目录段漏 check-prereq.cmd/negative/e2e；L33 已更新但目录结构未同步 | 最小更正 | P2 |
| CONTRIBUTING.md 产物名过时 | L14 旧 zip 名 | 改新版名 | P2 |
| Release 闭环 | tag→build→release、CHANGELOG regex 可靠、SHA256SUMS 附 body、REF_NAME env 注入 | 闭环 OK，无需改 | — |
| negative/E2E 不进 CI | build.yml 无调用 | **维持现状**（需已构建 exe+真实 GUI，属发布 gate 边界） | — |
| coverage | 方法论与覆盖率正交 | **不加** coverage 报告/badge | — |
| CI 分层/缓存 | 项目小 | **不加**（拆两套增加维护成本） | — |

---

## 11. 下一步发展路线

### P0：必须做

| # | 项 | 一句话 |
|---|---|---|
| P0-1 | 启动取消/启动中退出 → 服务无主残留（Program.cs:504-539） | canceled 分支补"已监听则 RecordServicePid"（或按确认改为清理）；取消码从 E9001 改独立码 |
| P0-2 | 静默崩溃无留痕 | 挂 UnhandledException/ThreadException 钩子写 E9001 日志 |

### P1：应该做

| # | 项 |
|---|---|
| P1-1 | 测试去重与删假绿灯：262 → 150-170（删 4 个永真 + 合并 CompareVersions/ResolveTarget/IsSafeToOpen/ShouldRotate + 合并 ReadLogTail/TailLines 实现） |
| P1-2 | 主题轮询 mtime 缓存（唯一运行期真热点） |
| P1-3 | `taskkill` 加 `/T`；`IsLikelyDshService` 追加端口监听校验 |
| P1-4 | window-state/pending 损坏补 Warn |
| P1-5 | CI 删双跑 dotnet test；`git rm --cached` 误追踪 DLL |
| P1-6 | 契约防线：抽 `IsReady` 纯函数 + HttpListener stub 测试；`IsLikelyDshService`/Node 门槛单测（C3/C9/C10） |
| P1-7 | 文档最小同步：DETAILS.md 过时点、CHANGELOG 0.3.0 口径、CONTRIBUTING 产物名、testing-governance §10 E3001 更正 |
| P1-8 | 人工调研：dsh 运行时身份假设（C9，潜在 P0） |

### P2：可以以后做

Process 对象 using 补全；UpdateChecker 网络失败 Info 日志；poll 传 CT；测试静态单例并行隔离治理约定；shell.log/theme.json 诊断说明；PowerModeChanged/SessionEnded；E1001 死码标注；tag 发布前 -Smoke 一次（可选）；发布包文件清单校验（可选）。

### 不建议做（明确）

- **Job Objects**：需重构启动链路、与 pid 认领机制冲突、两套清理哲学（收益已被 `taskkill /T` 覆盖）。
- **coverage 报告/badge**：方法论与覆盖率正交，纯"看起来专业"。
- **CI 分层/缓存/负向/E2E 进日常 CI**：项目小，噪音大于收益。
- **真实断网/杀软/UAC/多显示器热插拔自动化**：会误报或伤害环境，人工 checklist 已足够。
- **引入 mock 框架**：现有 FakeHttpMessageHandler 模式已够。
- **更新下载取消机制**：后台跑完是设计。
- **WebView2 显式 Dispose**：保持进程回收策略（人工确认即可，不折腾）。
- **任何新用户可见功能**：功能冻结。

---

## 12. 执行计划（P0/P1 逐项）

| 项 | 目标 | 涉及文件 | 修改方式 | 验证 | 回滚 | 用户可见 | 体积 | 测试数 | 风险 |
|---|---|---|---|---|---|---|---|---|---|
| P0-1 | 取消/启动中退出不产生无主服务 | `Program.cs`（canceled 分支 + E9001 语义）、`ErrorCodes.cs`、`docs/DETAILS.md` | canceled 分支：端口已监听则 `RecordServicePid()`；未监听则保留并保持现有文案；取消码改为独立码（如 E2006"启动已取消"）或并入 E2002 语义 | negative 套件新增用例：取消启动 → 断言服务可被下次接管/或已清理；手动 GUI 验证 | 单提交 revert | 取消后服务状态更可预期（仍可能继续下载，但可接管） | 0 | +1~2（负向） | 中：需人工确认取消语义（保留+接管 vs 清理） |
| P0-2 | 崩溃留痕 | `Program.cs`（Main 内 Logger.Init 之后） | 挂 `Application.ThreadException` + `AppDomain.UnhandledException`，写 `Logger.Error(msg, E9001, ctx)` | 负向用例：制造未捕获异常（可用 DSH_NO_UI 下抛异常钩子）→ 断言 dsh.log 有 E9001 记录 | 删除两行订阅 | 无（仅崩溃时多一条日志） | 0 | +1 | 低 |
| P1-1 | 测试资产清洗 | `tests/DshShell.Tests/` 4 个文件、`ShellLogic.cs`+`DiagnoseExport.cs`（合并 Tail 实现） | 删 4 个永真测试；合并 4 组重复；`ReadLogTail`/`TailLines` 合一 | `dotnet test` 全绿且断言数落 150-170；确认删除项无断言缺口 | 单提交 revert | 无 | 0 | -90~-110 | 低（需人工确认删除清单） |
| P1-2 | 主题轮询降频 | `Program.cs`（RegisterThemeWatcher / ReadDshThemePreference） | 按 mtime 缓存：`File.GetLastWriteTimeUtc` 变化才 `ReadAllLines`；Timer 间隔 500→2000ms（watcher 已兜底） | 手动：写 settings.yaml 主题偏好 → 2 个周期内生效；任务管理器观察无持续磁盘读 | 单提交 revert | 无（主题切换仍及时） | 0 | 0 | 低 |
| P1-3 | 进程杀灭加固 | `Program.cs`（KillProcess）、`ShellLogic.cs`（IsLikelyDshService） | taskkill 加 `/T`；身份校验追加"该 PID 监听 Target.Port" | 单测补 IsLikelyDshService 用例；负向验证 | 单提交 revert | 无 | 0 | +2~4 | 低 |
| P1-4 | 状态损坏告警 | `WindowStateStore.cs`、`StagedUpdate.cs` | Load/ReadPending 的 catch 分支补 `Logger.Warn`（对齐 settings.json 已有治理） | 单测：写损坏文件 → 断言日志 | 单提交 revert | 无 | 0 | +2 | 低 |
| P1-5 | CI 卫生 | `.github/workflows/build.yml` | 删"Run unit tests"独立步骤（test.ps1 已含） | CI 跑一次确认绿 | revert 该步骤 | 无 | 0 | 0 | 低 |
| P1-6 | 契约防线 | `Program.cs`（抽 `IsReady`）、`tests/` | 把轮询内就绪判定抽成纯函数；新增 HttpListener stub 契约测试（监听不响应/立即 200/延迟）；补 IsLikelyDshService、Node 门槛 stub 测试 | `dotnet test` 全绿 | 单提交 revert | 无 | 0 | +8~12 | 低-中（抽函数需小心保持行为一致） |
| P1-7 | 文档最小同步 | `docs/DETAILS.md`、`CHANGELOG.md`、`.github/CONTRIBUTING.md`、`docs/testing-governance.md` | 仅更正过时点（版本示例、目录清单、产物名、0.3.0 口径、E3001 描述） | 人工 review | 单提交 revert | 无 | 0 | 0 | 低 |
| P1-8 | 上游调研（人工） | — | 确认 dsh 是否可能更换运行时/Node 版本要求 | 调研结论记入 governance | — | — | — | — | — |

---

## 13. Definition of Done

- 关键测试稳定：`dotnet test` 262→150-170 全绿、单次运行无重跑，负向/E2E 断言保持绿。
- CI 不因环境波动频繁失败：删双跑、无环境依赖断言（已修脚）。
- 无已知内存泄漏：维持现状审计结论（无 P0 泄漏），Process 对象 using 补全后更干净。
- 无已知孤儿进程路径：P0-1 修复后，取消/启动中退出/崩溃/强杀四路径全部有接管或清理兜底。
- 关闭流程可诊断：崩溃钩子 + 现有统一日志。
- 错误码可追踪：E9001 不再承载"取消"语义；文档与代码错误码表一致（含 E3001 文档更正）。
- 日志不无限增长：轮转机制维持（30MB/>3 天），常驻超长告警维持。
- 不新增用户可见功能；不显著增加体积（全部改动合计 <200 行产品代码）；不引入重量级依赖（0 新依赖）。
- 不增加文档负担（只更正过时点，不新增章节）。
- 测试数量控制在合理范围（150-170），每条可指回代码行为。
- 项目比之前更容易维护：去掉双实现/双跑/假绿灯，契约缺口有 stub 防线。

---

*合并自 5 个子审计（测试资产/资源生命周期/进程与状态机/契约·性能·诊断/CI·发布·文档），主控逐条交叉验证。全文只读，未修改任何仓库文件。*
