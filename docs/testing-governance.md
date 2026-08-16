# dsh-launcher 测试治理 / Testing Governance

> 本文档是 dsh-launcher 的**测试治理方案**（A8 交付物）：不修改产品代码，只定义"怎么推导、怎么排优先级、测什么、自动化边界、CI 怎么做"。全部测试方向**以真实代码行为为唯一依据**（读 `src/DshShell/*.cs`、`scripts/start-dsh.vbs`、`scripts/uninstall-autostart.cmd`、`tests/DshShell.Tests/*.cs`、`.github/workflows/build.yml` 之后撰写），不臆造不存在的功能。
>
> 现状基线：v0.3.0 全部 P0 + v0.3.1 六项 P2 已完成；`tests/DshShell.Tests` 共 256 单测（
> ShellLogicTests / V030FeaturesTests / UpdateCheckerTests / DiagnoseExportTests / LoggerTests /
> SecurityBoundaryTests / ContractTests，按主题拆分；含 StagedUpdate 失败计数、Node 缺失原因等 v0.3.1
> 新增用例；P1-1 清洗后删除了永真假绿灯与跨文件重复，P1-6 新增上游契约测试），
> `scripts/test.ps1` 含 27+ 静态断言与 uninstall/-CleanData 隔离行为测试；`scripts/negative-test.ps1`
> 9 用例 23 断言（预期失败/隔离铁律，N9 崩溃留痕）；`scripts/e2e-test.ps1` 7 段 37 断言（发布产物 → 解压部署 →
> 真实 GUI → 窗口记忆 → 诊断导出 → 卸载 → 数据边界）。CI `build.yml` 跑 `test.ps1`（唯一测试入口，内部含 dotnet test）。

---

## 0. 一句话治理原则

**测试必须能指回一段代码行为。** 每条测试方向都能回答"防什么回归"，回答不了就不加；覆盖率优先覆盖 P0（无法启动/静默失败/进程泄漏/端口冲突/配置死锁/更新毁旧版/日志不可导出/UI 假死/内存增长/崩溃不可恢复）。方法论从五个来源推导问题空间，而不是维护一份会过时的固定用例清单。

---

## 1. 测试推导方法论（五源）

测试问题集按以下**五个推导源**生成；某个功能做小改时，对照五源推它波及的状态/故障/不变量，而不是手工遍历用例。

### 1.1 源 1：变更推导（Change-based）
任何代码改动 → 推导它触碰了哪些**纯函数 / 状态文件 / 环境变量 / 进程生命周期**。
- 每处改动问：改的是 `ShellLogic` 可注入纯函数，还是 `Program` 的事件接线，还是 `start-dsh.vbs` / `uninstall-autostart.cmd` 脚本？
- 纯逻辑改 → 单测；脚本改 → test.ps1 静态/行为断言；生命周期/UI 改 → 冒烟 + 人工。
- 推导时用本文件第 6 节的"Launcher vs 原生 dsh 差异"四条追问防止只测实现没测体验。

### 1.2 源 2：A2 用户旅程（User journey）
用户实际会走的路径，每条旅程都问"哪一步失败用户会卡住/感到困惑"。
- 冷启动（无服务）→ 拉起服务 → 开窗；二次点击单实例归位；托盘唤起；主题切换；下载文件；外部链接跳浏览器；同源弹窗；诊断导出；卸载/清理。
- 覆盖 `Program.Main`、`FormClosing`、`ShowMainWindow`、`InitWebViewAsync`、`DownloadStarting`、`NewWindowRequested` 等真实接线。

### 1.3 源 3：A3 故障域（Failure domain）
按错误码/故障类别枚举会"坏"的地方（对齐 §3.2 错误码约束：一个码贯穿弹窗/日志/诊断导出）。
- 运行环境（E1xxx：无 Node / 便携 Node 下载/校验/解压 / WebView2 缺失）→ 静态断言错误码。
- 服务/生命周期（E2xxx：vbs 缺失 / 启动超时 / 启动日志报错 / 服务不可用 / 僵尸清理 / 插件缺失降级）。
- 端口/网络（E3xxx：端口占用）。更新/下载（E4xxx：下载失败 / 延迟应用失败）。诊断（E5xxx）。内部（E9xxx）。
- 每条故障方向的测试都要**至少断言错误码在弹窗文本与日志 JSON `code` 字段一致**（§3.2 A-02 验收）。

### 1.4 源 4：A4 状态机（State machine）
本工程真实存在且最易回归的状态机：
- **延迟更新状态机**（`StagedUpdate` / `pending-update.json`）：`None → 下载成功 MarkPending → 下次启动 ReadPendingVersion → ApplyPendingDshUpdate 成功 ClearPending`；损坏文件 / npm 失败 → 保持可重试、不阻塞启动（E4002）。
- **生命周期模式状态机**（`ResolveEffectiveLifetime` / settings.json）：插件存在 vs 缺失 → 三种停留模式（常驻/托盘驻留/跟随窗口）在不同 UI 操作的迁转（关窗 / 托盘退出 / 托盘唤起）。
- **孤儿服务生命周期**（`RecordServicePid` / `SweepStaleServicePid` / `TryAdoptOrphanService` / `StopShellService`）：崩溃残留 → 接管/清理；关窗已停 vs 未停。
- **WebView2 崩溃自愈状态机**（`_lastReloadTick` 10s 节流 + `_webviewRecoveryNeeded` + `_hiddenSince` 5min）：渲染崩溃可视/隐藏、长隐藏强制重载。
- 状态机测试优先查"**跨会话持久态**"（文件落盘后下次启动正确恢复），因为单测跑的是进程内状态。

### 1.5 源 5：A5 系统不变量（System invariants）
贯穿全系统的、坏了不一定会报错但一定是 bug 的约束：
- **日志所有权唯一归壳**：统一日志 `DSH_HOME/dsh-launcher/dsh.log` 的 JSON 行与 dsh 输出共存；`start-dsh.vbs` append(8)、不截断、不轮转；旧 `.dsh-web.log` / `shell.log` 不再产生。
- **数据边界**：壳自有状态只在 `DSH_HOME\dsh-launcher\`；卸载/`-CleanData` **绝不触碰** `profiles/`、`settings.yaml`、`.credentials.yaml`、`sessions`、`storages`、插件等生态数据。
- **不变量回归防线（§4.1）断言到脚本**，因为它们既不是单测能测、也不是一眼能看的。

---

## 2. 风险优先级方法（P0/P1/P2）与测试方向字段

### 2.1 分级标准

| 等级 | 定义（命中任一即该级） |
| --- | --- |
| **P0** | 无法启动 / 静默失败 / 进程泄漏（服务残留） / 端口冲突（占用/抢占） / 配置死锁（settings.json 反复降级刷写） / 更新毁旧版（E4001/E4002 破坏运行中或下次启动） / 日志不可导出（--diagnose 失败） / UI 假死（关窗卡顿、白屏） / 内存增长（无界进程累积） / 崩溃不可恢复（重载死循环）。**每一条 P0 方向都必须有回归防线或明确的人工验证清单。** |
| **P1** | 明确可见但非致命的功能缺陷：单实例归位偶发失败、下载文件名/路径错位、托盘行为不符、主题切换偶发延迟、端口被他人占用时的引导不够清晰。有可重试/可绕行路径。需自动化回归（尽量）或人工核对。 |
| **P2** | 体验打磨/边缘：字体回退、DPI 极端值、超长日志告警口径、error code 文案措辞、错误码目录中存在但未落地的条目（见 §5 死码审计）。可接受低优先级或文档记录。 |

### 2.2 每条测试方向必带字段

> 设计决策：**不让"要不要自动化"遮蔽"测什么"**。先按以下字段完整描述一条方向，再判自动化归属。任何缺少下列字段之一的方向都不进入 §3 矩阵。

| 字段 | 说明 / 取值示例 |
| --- | --- |
| 测试目标 | 一句话：验证什么行为成立/被破坏 |
| 所属模块 | 对标到源文件/脚本（如 Program.Main、ShellLogic.RestoreWindowPosition、start-dsh.vbs、uninstall-autostart.cmd） |
| 风险级 | P0 / P1 / P2（按 §2.1） |
| 触发条件 | 前置状态 + 触发动作（对应具体代码分支） |
| 依赖环境 | 系统 Node 有无 / WebView2 有无 / 端口是否被占 / 多显示器 / 网络连通 / 是否需杀软或 UAC |
| 预期可观察结果 | 可被断言/肉眼识别的确定结果（弹窗文本含 [E####]、日志 code、窗口标题、进程是否残留、退出码） |
| 可收集证据 | 日志路径与关键字、诊断 zip、进程列表、注册表值、截图/录屏 |
| 是否可自动化 | 是 / 否（对 §5 边界：需真实断网/多显示器热插拔/杀软/UAC winget → 否） |
| 是否需人工 | 是 / 否（人工项给可勾选步骤与证据） |
| 失败诊断方式 | 该方向失败时如何定位：查 dsh.log 关键词 / 跑 --diagnose / 看进程列表 / 重放状态文件 |
| UX 影响 | 失败时用户看到什么、困惑度、是否有二次点击/白屏/无响应 |

---

## 3. 测试矩阵（推导产物）

> 按【用户旅程 × 故障域 × 状态机】组织；每行 1 句概要 + 风险级。**"回归防线 ✓"** = 现有 256 单测、负向 23 断言、E2E 37 断言或 test.ps1 已覆盖；未标注 = 缺口，其风险级即需要补的优先级。标注均对照真实代码核查。

### 3.1 冷启动 / 服务拉起（P0 重灾区）

| 用户旅程 | 推导方向（真实代码依据） | 风险级 | 状态 |
| --- | --- | --- | --- |
| 冷启动无服务 | 端口探测→僵尸清扫→延迟更新→Node 解析→vbs 拉起→状态窗等 HTTP 就绪 | P0 | ✓ E2E E3（真实 GUI 首启，隔离服务）；负向 N3（僵尸清扫） |
| 连续冷启动 | 本次 PID 写入 `service-pid-<port>.txt`，下次接管/清理 | P0 | **缺口** |
| 二次启动 | 单实例 Mutex→FindWindowEx→SW_RESTORE→前台 | P1 | 部分 ✓（test.ps1 冒烟断言第二进程退出） |
| 端口占用 | 端口开但 HTTP 不通→状态窗等待→E2002/E2003/E2004 | P0 | ✓ 单测 `LogShowsStartupError` / `ReadLogTail`；**缺口**：进程级端口冲突 |
| E1001 无 Node | `RuntimeResolver.ResolveExisting` null→`TryEnsureNodeAsync` | P0 | ✓ 单测 `BaseUrls`（镜像链）；**缺口**：E1001 无发射点（死码，见 §5） |

### 3.2 环境补齐 / 便携 Node（故障域 E1xxx）

| 推导方向 | 真实代码依据 | 风险级 | 状态 |
| --- | --- | --- | --- |
| 便携 Node 镜像回退链顺序与去重 | `RuntimeResolver.BaseUrls` 纯函数 | P1 | ✓ 单测 V030FeaturesTests 4 条 |
| 校验和不匹配拒绝（E1004） | `VerifySha256Async` 不符→TryDelete→E1004 | P0 | **缺口**（需真实/伪造 SHASUMS256，可隔离伪造） |
| 下载所有镜像失败（E1003） | `DownloadWithFallbackAsync` 全失败→E1003 | P0 | **缺口** |
| 便携 Node 碎片清理（取消/失败删临时目录） | `DownloadWithFallbackAsync` tmp、`TryDelete` | P2 | **缺口** |
| WebView2 缺失兜底（E1006 前静默装 Bootstrapper） | `TryInstallWebView2Async` 下载→/silent→重测 | P1 | **缺口**（依赖网络与真实安装，人工为主） |

### 3.3 配置 / 生命周期状态机（A4 ∩ 配置死锁 P0）

| 推导方向 | 真实代码依据 | 风险级 | 状态 |
| --- | --- | --- | --- |
| 插件缺失时 serviceLifetime 降级并抹除 | `ResolveEffectiveLifetime` + `PurgeServiceLifetime` | P0 | ✓ 单测 V030FeaturesTests |
| 插件存在时三种模式解析且不抹除 | `ParseLifetimeMode` | P1 | ✓ 单测 |
| 插件检测（entity/manifest/broken→安全默认） | `IsLifetimePluginInstalled` | P1 | ✓ 单测 V030FeaturesTests |
| 关窗/托盘退出时停在正确模式 | `FormClosing`→Tray/FollowWindow/AlwaysOn 分支 | P0 | **缺口**（UI 接线，人工为主） |
| settings.json 损坏→回退跟随窗口，不崩溃 | `ReadLifetimeMode`→try/catch 回退 | P0 | ✓ 单测 `ParseLifetimeMode_*FallsBack` |
| 旧版 settings.json 路径迁移 | `ReadLifetimeMode` legacy 读取+迁写+删 | P1 | **缺口** |

### 3.4 延迟更新状态机（A4 ∩ 更新毁旧版 P0）

| 推导方向 | 真实代码依据 | 风险级 | 状态 |
| --- | --- | --- | --- |
| GitHub/npm 版本拉取（JSON 解析/失败静默/限流） | `UpdateChecker.FetchLatest*` | P1 | ✓ 单测 UpdateCheckerTests（FakeHttpMessageHandler 注入，16 条） |
| 安全/重要更新判定（SECURITY/-sec 标记） | `FetchLatestLauncherReleaseAsync` | P1 | ✓ 单测 UpdateCheckerTests（body/tag 两种标记、普通版不误报） |
| 语义化版本比较（防误报打扰用户） | `CompareVersions` | P1 | ✓ 单测 UpdateCheckerTests（10 条边界，含非法值按 0.0.0） |
| 本地 dsh 版本解析（环境变量优先） | `ResolveLocalDshVersion` | P2 | ✓ 单测 UpdateCheckerTests |

| 推导方向 | 真实代码依据 | 风险级 | 状态 |
| --- | --- | --- | --- |
| MarkPending/Read/Clearn 往返与损坏文件 | `StagedUpdate` 单测 | P1 | ✓ 单测 V030FeaturesTests |
| 下载成功写 pending，下次启动应用成功清记录 | `DownloadDshUpdateStaged` + `ApplyPendingDshUpdate` | P0 | **缺口**（e2e：真实 npm pack/install） |
| 应用失败(挂 pending)→下次重试、不阻塞启动 | `ApplyPendingDshUpdate` 失败→`Logger.Warn(E4002)`，`ClearPending` 不执行 | P0 | **缺口** |
| 下载失败（E4001）不打断当前会话 | `DownloadDshUpdateStaged` else→E4001 | P0 | **缺口** |
| pending-update.json 损坏按无记录 | `ReadPendingVersion` catch→null | P1 | ✓ 单测 `StagedUpdate_CorruptFile` |

### 3.5 孤儿/僵尸进程生命周期（A4 ∩ 进程泄漏/端口冲突 P0）

| 推导方向 | 真实代码依据 | 风险级 | 状态 |
| --- | --- | --- | --- |
| 端口未开时清扫 stale PID（只动记录的） | `SweepStaleServicePid` | P0 | **缺口**（进程级） |
| 端口残留健康→接管；HTTP 不通→清理 | `TryAdoptOrphanService` | P0 | **缺口** |
| 跟随窗口关窗停服务（同步，任务不后置） | `StopShellService`→`KillProcess` 同步 | P0 | **缺口**（曾因后台 Task 延迟导致残留，见代码注释） |
| KillProcess 三级降级（CTRL_BREAK→taskkill→/f） | `TryGracefulStop`+`KillProcess` | P1 | **缺口** |

### 3.6 窗口 / UI（A2 ∩ UI 假死 / 崩溃不恢复 P0）

| 推导方向 | 真实代码依据 | 风险级 | 状态 |
| --- | --- | --- | --- |
| 多显示器位置恢复（越界居中/钳制/负坐标） | `RestoreWindowPosition` 纯函数 | P1 | ✓ 单测 V030FeaturesTests 7 条 |
| 窗口位置/大小记忆（关闭写回、重启恢复） | `SaveWindowState`/`WindowStateStore` | P1 | ✓ E2E E4（真实 GUI 移动→关闭→重启恢复一致）|
| `SplitLParam` 负坐标不抛 Overflow | `SplitLParam` | P1 | ✓ 单测（4+1 条，含 B1 回归） |
| WebView2 渲染崩溃自动重载（10s 节流，防死循环） | `ProcessFailed`+`Interlocked` 节流 | P0 | **缺口** |
| 长隐藏(>5min)恢复强制重载防白屏 | `ShowMainWindow` longHidden | P1 | **缺口** |
| 关窗不 Dispose WebView2（避免 1-2s 卡顿） | `FormClosing` 注释 | P1 | **缺口**（人工体感） |
| 无边框自绘标题栏/缩放/Snap 复位 | `DshShellForm.WndProc` WM_NCCALCSIZE/NCHITTEST | P2 | **缺口**（人工） |

### 3.7 下载 / 弹窗 / 权限（A2 ∩ 故障域，多数已单测）

| 推导方向 | 真实代码依据 | 风险级 | 状态 |
| --- | --- | --- | --- |
| 下载文件名推导/清理/Windows 保留名 | `SuggestDownloadName`+`SanitizeFileName` | P1 | ✓ 单测（90 条左右 Theory + SecurityBoundaryTests 边界） |
| 可执行面不自动打开（S2） | `IsSafeToOpen` | P1 | ✓ 单测（15 条 + SecurityBoundaryTests 22 条可执行面全拒绝） |
| 弹窗分类 外链/同源/blob | `ClassifyPopup` | P1 | ✓ 单测（13 条） |
| 内部弹窗共享会话/登录态 | `NewWindowRequested`+`CreatePopupForm` | P1 | **缺口** |
| 权限自动放行白名单 | `IsAutoGrantedPermission` | P1 | ✓ 单测（12 条 + SecurityBoundaryTests 全枚举精确匹配） |
| 导航白名单 S3（外部导航转浏览器） | `NavigationStarting` 取消外链 | P0 | **缺口**（安全，建议优先） |

### 3.8 日志 / 诊断 / 数据边界（A5 ∩ 日志不可导出 P0）

| 推导方向 | 真实代码依据 | 风险级 | 状态 |
| --- | --- | --- | --- |
| 统一日志单文件、append(8)、旧路径不再产生 | `Logger` + test.ps1 静态断言 | P1 | ✓（脚本静态断言多行 + LoggerTests 结构/级别/静默） |
| 轮转判定（30MB/>3天，保留≤3） | `Logger.ShouldRotate` 纯函数 | P1 | ✓ 单测（V030FeaturesTests 4 条 + LoggerTests 阈值边界） |
| Write 失败静默不影响启动 | `Logger.Write` try/catch | P0 | ✓（LoggerTests 路径阻塞不抛 + 负向 N4 进程级） |
| `--diagnose` 生成脱敏 zip、无凭据 | `DiagnoseExport` 白名单/Sanitize | P0 | ✓ 单测 DiagnoseExportTests（Sanitize/Tail/汇总）+ 负向 N5/N8 + E2E E5（进程级全链路） |
| `--min-level warn/error` 过滤 | `DiagnoseExport.FilterByLevel` | P1 | ✓ 单测 DiagnoseExportTests（级别过滤/错误标志/参数解析） |
| 卸载数据边界（只清自有、不动生态） | `uninstall-autostart.cmd -CleanData` | P0 | ✓ test.ps1 §3.5 隔离测试 |
| 升级旧版清理/孤儿快捷方式/自启 不误删他人 | `PickOldInstalls`+`IsOurShortcutTarget` | P0 | ✓ 单测（PickOld 8 条 + shortcut 3 条）；**缺口**：真实注册表场景 |

### 3.9 运维 / 注册表 / 单实例

| 推导方向 | 真实代码依据 | 风险级 | 状态 |
| --- | --- | --- | --- |
| 自启写完 HKCU、路径变化自愈、Win/vbs 兼容 | `EnsureAutoStartRequested` | P1 | **缺口**（注册表，人工） |
| 清理孤儿自启（Run 指向不存在文件） | `CleanupOrphanShortcuts` | P2 | **缺口** |
| DSH_WEB_URL 外部托管时不拉起服务 | `ServerManagedExternally`+`ResolveTarget` | P1 | ✓ 单测 `ResolveTarget` |

---

## 4. 回归防线清单（端到端 / 脚本级断言 ≤15 条）

> 规则：每条必须能回答"**防什么回归**"，并指向一段代码行为；**总数超过 15 条则砍旧补新**；临时/体验类不加。现有 test.ps1 已含 27+ 断言，本清单是其**主干子集**（不重复罗列全部 27，聚焦防回归价值高的）。

| # | 断言 | 防什么回归 | 验证方式 |
| --- | --- | --- | --- |
| R01 | `DshWeb.exe --diagnose` 在下载目录生成 `dsh-launcher-diagnose-<ts>.zip`，内含 `env.txt`/`errors.txt`/`log-full.txt` | 诊断导出失效 → 用户无法打包定位（P0 日志不可导出） | 进程脚本断言：跑 .exe → `Test-Path` + 解包 `Select-String` 看条目 |
| R02 | `errors.txt` 中每个错误码均匹配 `ErrorCodes.Describe` 且与日志 `code` 字段一致 | 弹窗码/日志码/诊断码三者漂移 | 脚本断言：比对 errors.txt 计数行与 dsh.log JSON `code` |
| R03 | `SweepStaleServicePid` 只清理 pid 文件记录的 PID，不按进程名批量杀 | 误杀用户自启的其它 node 进程（进程泄漏/误杀） | 脚本断言：伪造 stale pid → 断言其被清、同名无关进程存活 |
| R04 | 跟随窗口关窗后，本次 `_serviceStartedByShell` 的端口监听进程被同步停止（≤2s 内无监听、pid 文件清理） | 关窗后服务残留占端口（历史 issue） | e2e：启动→关窗→`netstat -ano` 断言 3080（或 DSH_WEB_PORT）无监听 |
| R05 | `uninstall-autostart.cmd` 不带参数**不清** `DSH_HOME\dsh-launcher`，带 `-CleanData` 才清且不动 `profiles/` | 卸载 surprise-delete 用户/插件数据（数据边界 P0） | test.ps1 §3.5 隔离测试（已✓，纳入防线基线） |
| R06 | `start-dsh.vbs` 以 append(8) 写 `DSH_LOG` 指向的 `dsh-launcher\dsh.log`，不截断不轮转、不再写 `.dsh-web.log` | 日志双写/所有权冲突/轮转双所有者 | test.ps1 §2 静态断言（已✓） |
| R07 | 旧版 `settings.json` 迁移到新路径后旧位置被删、新位置可用 | 路径错位导致"选常驻却按跟随窗口跑" | e2e/人工：造旧位置→启动→断言新旧位置状态 |
| R08 | 插件缺失且 settings.json 含残留 `serviceLifetime` → 降级跟随窗口且文件被幂等抹除 | 配置死锁/反复刷写 | ✓ 单测 `ResolveEffectiveLifetime`；e2e 补文件级 |
| R09 | 单实例二次启动不创建第二个窗口/不残留 WebView2 进程 | 多开 WebView2 白白占内存（内存增长） | test.ps1 冒烟 + e2e 计数 msedgewebview2.exe 子进程 |
| R10 | 渲染崩溃重载 10s 节流：连续崩溃不无限 Reload | 崩溃死循环（崩溃不可恢复 P0） | e2e/人工：杀渲染进程 → 断言窗口 10s 内仅一次重载 |
| R11 | `--diagnose` 产物中不含 `.credentials.yaml`/sessions/storages 内容，用户目录被 `%USER%` 替换 | 诊断导出泄漏敏感数据（A-09） | 脚本断言：解包 grep 敏感关键字/路径 |
| R12 | WebView2 缺失兜底：触发→下载 Bootstrapper→重测→成功才不弹 E1006 | 真缺 WebView2 时静默无窗口 或 弹窗误导 | 人工（破坏性，需移除/隐藏 WebView2 目录） |
| R13 | 延迟更新应用失败（E4002）→ pending 保留、继续用旧版、下次启动可重试、不阻塞 | 更新失败毁掉可用版本的启动 | e2e（stub npm）或人工：断言日志 E4002 且窗口仍打开 |
| R14 | 自启点 Run 指向 `DshWeb.exe`；`AutoStartWanted=1` 且 Run 缺失/旧格式时壳自愈补写 | 自启失效/死项白启（保证 HKCU 100% 可靠） | 人工（注册表 HKCU Run） |
| R15 | 主题切换（settings.yaml 写偏好）→ 标题栏/图标/theme.json 在 2 个轮询周期内更新 | 主题切换即时生效、无残留旧色 | 人工 + theme.json 断言 |

---

## 5. 自动化 vs 人工边界

### 5.1 可自动化（纯逻辑 / 进程级脚本断言 / 隔离伪造环境）

**黄金约束（强制）**：任何伪造 `DSH_HOME`/`USERPROFILE` 的脚本测试，**伪造路径必须落在 `%TEMP%` 内，且先做前置防护断言**——伪造 DSH_HOME 的 `GetFullPath` 必须 `StartsWith($env:TEMP)`，否则拒绝执行（防误删真实数据）。已踩历史事故：`%~dp0`/解析期空变量导致误删盘根（commit 631adc8），本地 `uninstall-autostart.cmd` 曾三次误删 `E:\dsh-launcher`；§3.5 test.ps1 已落实此防护并纳入防线 R05。

可自动化的类别：
1. **纯逻辑单测**：`ShellLogic` 全部静态函数（弹窗分类/文件名/权限/窗口恢复/旧版卸载选择/lifetime 解析/日志轮转/镜像链）。**已 140 单测覆盖**。
2. **进程级脚本断言**：diagnose 产物存在与内容、错误码契约、进程残留/端口监听、pid 清理、单实例、@%TEMP% 隔离的 uninstall/-CleanData 行为。
3. **隔离伪造环境**：用临时目录 + 隐藏系统 Node + stub npm/伪造 SHASUMS256 逆推 E1003/E1004、关窗停服务、stale pid 清理。
4. **WebView2 数据目录隔离铁律**：测试实例必须设置 `DSH_WEBVIEW2_DATA` 指向隔离目录——多个进程共用
   同一 user-data-dir 会互锁，导致真实启动器 UI 线程卡死、整窗灰色无响应（2026-08-16 实测事故，负向/
   E2E 全部用例已强制隔离）。
4. **Portable/Mock 服务**：`--diagnose`/端口冲突可用内存 HTTP stub 测 E2004/状态窗等待，不依赖真 dsh 服务。

### 5.2 需人工（真实环境才能成立，自动化会误报或伤害环境）

| 类别 | 为什么不能自动 | 人工验证清单模板 |
| --- | --- | --- |
| 多显示器热插拔 / 跨 DPI / 负坐标 | 缺真实副屏；自动化无法复现拔屏 → 窗口越界 | 启用第二屏→移窗→拔屏→重开→断言在主屏居中可见 |
| 真实断网 / 弱网 | 自动模拟不了真实限流/超时/镜像降级 | 断网启动→断言 E2003/E1003 文案、可取消、不假死；恢复网络→可重试 |
| 杀软拦截 | 杀软会拦 WebView2/Node 下载/静默安装 | 开杀软→断言不静默挂起、给出真实错误弹窗 |
| UAC / winget 装 .NET（PrereqCheck） | 需要管理员弹框与 winget | 在无 .NET 机器走安装→点"自动安装"→断言成功装完并继续 |
| 精简系统缺 WebView2（E1006 兜底） | 需真实无 WebView2 环境 | 移除/隐藏 WebView2 Runtime→断言 Bootstrapper 静默装成功或弹 E1006 |
| 导航/弹窗到真实外链 | 需 web 会话验证浏览器跳转 | 点外部链接→断言浏览器打开且壳无残留页面 |
| 真实 npm install -g 更新 | 需真实 npm 生态验证 E4001/E4002 与版本漂移 | 触发延迟更新→重启→断言新版本拉起、无会话丢失 |

> 人工验证结论统一记到 Issue/PR 描述末尾的 checklist（勾选式），作为发布 gate。

---

## 6. Launcher vs 原生 dsh 体验差异验证

> 壳的目标是"**无感原生**"：比直接开浏览器的额外东西必须最小化。四条追问逐条验证 launcher **不引入额外延迟/弹窗/资源/困惑**。

| 差异面 | 原生 dsh / 浏览器基线 | Launcher 现状（真实代码） | 验证项 |
| --- | --- | --- | --- |
| **更新入口移动**（气泡 → 延迟应用） | 直接 npm update | `ScheduleUpdateCheck`→托盘气泡→`PromptDshUpdate`→`DownloadDshUpdateStaged` 下载到 staging，`ApplyPendingDshUpdate` 下次启动才装 | ①确认"下载完成不打断当前会话"真的成立（下载期服务不重启、会话不丢）②气泡只弹一次、不重复打扰 ③普通 launcher 更新不推送（仅安全更新）④无更新/网络失败完全静默（无额外弹窗） |
| **停止方式**（taskkill 链） | Ctrl+C 前台 | `KillProcess`：CTRL_BREAK→温和 taskkill→1.5s 后 /f，同步限时 | ①关窗在 ~1s 内完成，无卡顿（UI 假死 P0）②服务确实停止、无端口残留 ③不强杀用户同名 node（只杀记录的 PID） |
| **日志位置** | 无 / 散落 | 统一 `DSH_HOME/dsh-launcher/dsh.log`（= `~/.dsh/dsh-launcher/dsh.log`），JSON+服务输出共存 | ①日志文件名/路径与文档一致 ②old `.dsh-web.log`/`shell.log` 不再生成 ③`--diagnose` 能从同一文件导出 |
| **环境补齐**（便携 Node） | 需手动装 | `RuntimeResolver` PATH→注册表→便携；缺失一次性确认→下载 LTS zip→SHA256→解压 → 进程级前置 PATH | ①只前插进程级 PATH，不改系统环境/注册表 ②系统 Node>=18 优先、便携仅作兜底 ③校验失败拒绝使用（E1004）④取消一次性都不再打扰 |

---

## 7. P0 优先顺序与前置依赖

> 决策：**P0 里先铺"必须先行"的基石测试**，动了它们才能安全重构；重构之后这些又变成回归防线。

### 7.1 动重构之前必须先完成（前置地基）
1. **日志/错误码契约测试（R02 + --diagnose R01/R11）**：所有 P0 诊断依赖它；不先锁契约，重构后无法判断故障是"真的回归"还是"诊断丢了"。
2. **数据边界隔离测试（R05）**：防止任何重构把数据写到 `DSH_HOME` 生态区。
3. **进程残留防线（R03/R04）**：僵尸/孤儿/停服务三者，是"无法启动/端口冲突"的两大来源，须在改生命周期前锁定当前可停/可接管行为。
4. **延迟更新状态机（§3.4）**：先固化"失败不毁旧版"的契约，之后改动应用逻辑才有对照。

### 7.2 重构之后作为回归防线（后置回归带）
- 生命周期模式重构 → 跑 §3.3/§3.5 状态机测试。
- ShellLogic 纯函数重构 → 140 单测 + 新增 Theory。
- start-dsh.vbs/uninstall 改动 → test.ps1 §2/§3 静态+行为断言（防线 R06/R05）。
- 更新机制改动 → §3.4 + R13。

---

## 8. CI 集成建议（克制，只列必要项）

> build.yml 当前 `push master`/`PR` 只跑 `dotnet test` 再 build-release。建议克制地增量，避免拖慢每次构建。

### 8.1 必加（低成本、高防回归价值）
1. **`./scripts/test.ps1` 无 -Smoke 段纳入 CI**：它已含 27+ 静态断言 + uninstall/-CleanData 隔离行为测试，纯只读+临时目录，可在 runner 安全跑。理由：把 §3.5 数据边界与脚本行为变成每次 PR 的 gate（R05/R06）。
   ```yaml
   - name: Run script regression + isolated behavior tests
     shell: pwsh
     run: ./scripts/test.ps1
   ```
2. **诊断产物断言**（R01/R02/R11）：CI 里跑 `DshWeb.exe --diagnose` 难（需已发布 exe），可退而求其次在构建 release 后、上传 artifact 前加一步对 dist 包执行 diagnose 冒烟（可选，见 8.2）。

### 8.2 可选 / 用 gate 保护（有副作用或慢，默认不开）
3. **dist 冒烟（test.ps1 -Smoke）**：`build-release.ps1` 后对 dist 跑 -Smoke。它只断言窗口标题+单实例+进程残留（不真连 dsh，靠 3080 预装与否决定 SKIP），可加 `continue-on-error` 或做成独立 job，避免拖慢 build 主链。
4. **代码签名/内容校验**：`SHA256SUMS.txt` 已生成；可在 CI 校验 zip 内文件清单与脚本断言一致（防发布包缺 start-dsh.vbs 等，防 R06 漂移）。

### 8.3 明确不加进 CI（交给人工 §5.2）
- 真实断网 / 杀软 / UAC winget / WebView2 缺失 / 多显示器热插拔——**都需要真实环境**，自动化会误报或伤害 runner，放人工 checklist 作为发布 gate。

---

## 9. 附件：测试方向字段示例（一条完整展开）

**方向：跟随窗口关窗停服务（来源 3.5）。**
- 测试目标：验证关窗后壳本次拉起的服务被同步停止、无端口残留。
- 所属模块：`Program.StopShellService` / `Program.KillProcess` / `Program.FormClosing`。
- 风险级：**P0**（进程泄漏/端口冲突）。
- 触发条件：壳托管拉起服务（`_serviceStartedByShell=true`，已 `RecordServicePid`）→ 用户关闭主窗口。
- 依赖环境：系统 Node 或便携 Node + dsh 可拉起；`DSH_WEB_PORT` 指定空闲端口。
- 预期可观察结果：关窗后 ≤2s 内 `netstat -ano` 无该端口 LISTENING；`service-pid-<port>.txt` 被删除；壳进程退出。
- 可收集证据：`netstat` 快照、进程列表、`dsh.log` 的 `main loop exited` 与停服务日志、pid 文件存在性。
- 是否可自动化：可（进程级脚本断言 + 临时端口）。
- 是否需人工：基线一次人工确认后转自动化回归（R04）。
- 失败诊断方式：停服务日志缺失 → 查 `KillProcess` 分支；pid 文件残留 → 查 `ClearServicePidFile`。
- UX 影响：失败则用户关窗后服务仍在 → 端口占用、下次启动进入孤儿接管路径、内存泄漏感。

---

## 10. 已知审计关注点核对（以代码为准）

- **弹窗崩溃连锁主窗口**：`NewWindowRequested` 内部弹窗走 `CreatePopupForm` 独立 Form + 独立 WebView2 控件，与主窗口共享环境非共享控件；popup 崩溃不影响主窗。风险 P1，防御点：确保 popup 的 `FormClosing` 只 Dispose 自身 WebView2（已由 `form.FormClosing += Dispose(popupWeb)` 保证，§3.6/§3.7）。
- **pending-update 永续场景**：`ApplyPendingDshUpdate` 应用失败时不 `ClearPending` → 每次启动重试且 `npm install -g` 最多 120s。若版本已不可用则每次启动都拖 120s 延迟——**潜在 P1 永续延迟**，建议加诊断关注、引入最大重试次数（设计建议，不在本文件改代码）。
- **崩溃恢复循环无上限**：`ProcessFailed` 用 10s 节流（`_lastReloadTick`），有上限但无"连续 N 次后放弃"；极端崩溃仍是重载循环。风险 P0 边界，人工确认是否需熔断（设计建议）。
- **配置损坏静默回退**：settings/pending/window/runtime-state 损坏一律 try/catch 回退，安全但**静默**——仅 `ResolveEffectiveLifetime` 走 `Logger.Warn(E2011)`，window-state/pending 损坏无告警。风险 P1（可诊断性），建议已实现。
- **下载无重试**：便携 Node 镜像链是**静态回退链**（无重试），`DownloadDshUpdateStaged` 也是失败即报 E4001。符合"克制、不常驻重试"哲学，但弱网一次失败即需用户重按。风险 P2（有意设计，文档明确）。
- **错误码疑似死码（已清理，v0.3.1+ 修订）**：`E1001`（无 Node）与 `E3001`（端口被占）在 0.3.0 已随死码清理删除（见 CHANGELOG 0.3.0"删除死码"），本段旧表述作废；`E9001`（内部未分类）此前唯一发射点是 `waitResult` switch 兜底分支且"取消启动"被错误归入——P1-1 质量治理已改：`canceled` 使用独立码 **E2006**（启动已取消），E9001 仅保留给真正的内部未分类异常（含 P0-2 崩溃留痕钩子）。风险 P2。

---

*本文件由 A8 测试治理推导产出，仅供治理参考；不修改任何产品代码。*
