# 系统因果地图（System Causal Map）

> **铁律**：修复任何跨模块 Bug 时，Agent **必须**先在本地图中定位 Bug 发生的节点，
> 并检查上下游的身份传递（`DshRuntimeIdentity`）是否一致。

---

## 0. v0.4.x 用户回归修复批注（2026-09：静默失败 / 开窗慢 / Release 空日志）

> 本次修复在因果链中的落点与身份一致性检查记录（铁律第 3 步要求）。

### 落点 1：启动编排链（"点击很久才有窗口"）
```
双击 → [Stage4 单实例] → Splash(OnShown 后台流水线)
     → EnsureRuntime → 探测端口 → 拉起(vbs) → WaitServiceReady
     → 【修复①】LauncherApp 就绪预算判定不再于 UI 续体同步 DiscoverCurrentRuntime()
       （全局安装场景 spawn node --version 数百 ms～3s 曾冻结 Splash）
     → Splash 关闭 → 【修复②】EnsureServiceAndRuntime 尾部：
       · 回滚武装 ArmUpdateRollbackGuardFromPersistedState 移入 Task.Run（内含身份发现）
       · 删除重复 PortOpen 同步终检（就绪真相源 = 流水线 outcome.Ready 的 TCP+HTTP 双探针）
     → 建主窗（目标 ≤300ms）
```
**身份传递检查**：WaitServiceReady 的预算判定仍读 `DiscoverCurrentRuntime().Source`；
发现层只记忆化**昂贵版本探测**（ProbeVersionOutput），`DSH_VERSION` 钩子保持每次即时生效，
UpdateChecker/ReadGlobalDshVersion/vbs 三级回退链语义零变化。

### 落点 2：首装链（无 dsh → "静默失败"）
```
Source==NpxCache →【改道】TryEnsureGlobalDshInstalled：
  npm install -g @deepseek-ai/dsh@<ver|latest>
  · 共享预算 ShellLogic.ProvisionPolicy（600s 总额/420s 单源/剩余<60s 放弃）
  · 换源边界 → Splash 黄色告警文案（降级可见化）
  · 成功 → DshDiscovery.InvalidateCache() → StartDshServiceViaVbs（发现链立见新 shim）
  · 失败 → 返回 false（不落 npx 冷路径）→ HandleStartupFailure 经
    StartupFailurePolicy 映射为 [E1012] 展示真实根因
```
**身份一致性**：安装成功必调 InvalidateCache——否则 WaitServiceReady 会按 NpxCache 误用
360s 预算、UpdateChecker 读到旧版本。旧 SelfContained staging 双份构建代码已删除；
更新引擎（DownloadDshUpdateStaged→staging 构建→原子应用）不受影响。

### 落点 3：单实例与崩溃出口（"双击没反应/无声消失"）
```
第二实例：等待主窗 20s→5s；超时弹 [E1009] Info（不再静默 return false）
AppDomain.UnhandledException：写 E9001 日志后 TryShowFatalDialog 可见化（非无头模式）
```

### 落点 4：发布链（Release 正文为占位符）
```
tag v0.4.0 首推指向无 changelog 小节的提交 → fallback 文案被发布；
事后强移 tag 触发的 CI 因测试红未能刷新 Release。
根治：build.yml 发布 job 对缺失条目 exit 1（fail-fast 闸门）；CI 测试红修复；
流程纪律见 .github/CONTRIBUTING.md「发布流程与 tag 纪律」。
```

---

## 1. 自动更新因果链

用户打开壳 → 检测新版 → 下载 → 下次启动自动安装 → **验证 Identity 真的变了**。

> **Scenario 文档**：`docs/scenarios/update-dsh.md`（含 7 个 False Positive 陷阱详解）

```mermaid
graph TD
    subgraph "Phase 1: Detection"
        T["用户打开壳"] --> A
        A["① DshDiscovery.DiscoverCurrentRuntime()"] -->|"📦 证据: Identity 对象"| B
        B["② FetchLatestDshVersionAsync(http)"] -->|"📦 证据: remoteVersion"| C
        C["③ CompareVersions(remote, local) > 0?"] --> D{"需要更新?"}
    end

    subgraph "Phase 2: Download"
        D -->|"是"| E["④ DownloadDshUpdateStaged()"]
        E -->|"📦 证据: File.Exists(tarball)"| F["⑤ MarkPending(version, tarball)"]
        F -->|"📦 证据: ReadPending().Version"| G["托盘气泡"]
    end

    subgraph "Phase 3: Apply（下次启动）"
        H["下次启动"] --> I["⑥ ReadPending()"]
        I --> J["⑦ ApplyPendingDshUpdate()"]
        J --> K["⑧ RunNpmCommand('install -g ...')"]
        K -->|"📦 证据: exit code + stdout"| L{"npm 返回 0?"}
    end

    subgraph "Phase 4: Verify（终极验证）"
        L -->|"是"| M["⑨ ClearPending()"]
        M --> N["⑩ StartDshServiceViaIdentity()<br/>ServiceManager.Start(identity)"]
        N --> O["⑪ ServiceManager.PollReadiness()"]
        O -->|"📦 证据: HTTP 响应"| P["⑫ DshDiscovery.DiscoverCurrentRuntime()"]
        P --> Q{"identity.Version<br/>== targetVersion?"}
        Q -->|"是"| R["✅ 真正成功"]
        Q -->|"否"| S["❌ FP1: Identity 未变<br/>npm 成功 ≠ 更新成功"]
    end

    L -->|"否"| T2{"IsRetryableNpmError?"}
    T2 -->|"是"| U["保留 pending"]
    T2 -->|"否"| V["ClearPending + 弹窗"]

    style R fill:#2d6a2d,color:#fff
    style S fill:#8b0000,color:#fff
```

### 观察证据（Evidence）清单

| 节点 | 证据 | 获取方式 | False Positive 陷阱 |
|---|---|---|---|
| ① Identity | `DshRuntimeIdentity` | `DiscoverCurrentRuntime()` | — |
| ② 远端版本 | string | HTTP GET registry | 网络失败 → null → 静默跳过（合理） |
| ③ 比较结果 | int | `CompareVersions()` | FP6: 重复提示（去重逻辑防） |
| ④ tarball | 文件存在 | `File.Exists()` | — |
| ⑤ pending | JSON 记录 | `ReadPending()` | FP7: 死循环（IsRetryableNpmError 防） |
| ⑧ npm 执行 | exit code | `RunNpmCommand` 返回值 | **FP1: exit 0 ≠ Identity 变化** |
| **⑫ 最终 Identity** | **`DshRuntimeIdentity`** | **`DiscoverCurrentRuntime()`** | **FP1: 必须 == targetVersion** |

### False Positives 陷阱

| ID | 陷阱 | 场景 | 拦截方式 |
|---|---|---|---|
| **FP1** | **npm 成功但 Identity 未变** | `npm install -g` 返回 0，但运行的是 npx 缓存 | ⑫ 必须验证 Identity |
| FP2 | npm 成功但装了错误版本 | `install -g` 不带版本号 → latest | `installSpec` 必须精确版本 |
| FP3 | 安装成功但服务未重启 | 旧进程仍在运行 | 先 StopShellService 再 Start |
| FP4 | 服务重启但 HTTP 未就绪 | 新版本启动失败 | WaitServiceReady 超时 |
| FP5 | pending 被清但实际未更新 | ClearPending 在 Identity 验证前 | **先验证 Identity 再 ClearPending** |
| FP6 | 更新成功但重复提示 | 检测走了不同路径 | `_sessionStagedVersions` 去重 |
| FP7 | 更新失败导致死循环 | 不可重试错误未清 pending | `IsRetryableNpmError` 分类 |

**关键身份传递节点**：
- **① → ③**: `identity.Version` 决定"是否有更新"
- **① → ⑧**: `identity.Source` 决定"用什么命令安装"
- **⑩**: `identity.NodeExePath × DshEntryJsPath` 决定启动命令（`ShellLogic.ServiceLaunch.BuildArgs`；ADR-024 后唯一合法来源）
- **⑫**: **终极验证** — Identity 必须等于目标版本（防 FP1；引擎侧由 `LogPostApplyIdentity` 留痕）

**L3 测试保护**：
- `Update_Changes_Actual_Running_Identity`（SystemUpgradeOutcomeContracts）— 验证 ⑫ 的 Identity 物理切换
- `Outcome_Update_NpmSuccess_WithoutIdentityChange_IsFalsePositive` — 专门拦截 FP1
- `Outcome_Update_DetectionAndVerification_UseSameIdentity` — 锁定 ① 和 ⑫ 同源
- **P**: 最终验证必须基于 `DshDiscovery`，而非独立探测

---

## 2. 服务启动因果链

用户双击 `DshWeb.exe` → 服务就绪的完整因果路径。

```mermaid
graph TD
    A["Program.Main()"] --> B["InitializeProcessEnvironment()<br/>DPI + WinForms"]
    B --> C["HandleCommandLineArgs()<br/>--diagnose / --ui-selftest / --ui-probe"]
    C --> D["InitializeCoreDataAndLogs()<br/>Logger + CrashHooks"]
    D --> E["EnsureSingleInstanceAndAutostart()<br/>Mutex + 旧版本清理"]
    E --> F["EnsureServiceAndRuntime()<br/>SplashForm 流水线"]
    F --> G["LauncherApp.RunStartupAsync()"]
    G --> H["BackgroundMaintenance(ct)<br/>日志轮转/数据迁移/延迟更新"]
    H --> I["RuntimeManager.EnsureRuntimeAsync()<br/>Node.js 环境解析"]
    I --> J{"Node.js 可用?"}
    J -->|"是"| K["ServiceManager.ProbePort()"]
    J -->|"否"| L["下载便携 Node.js"]
    L --> K
    K --> M{"端口状态?"}
    M -->|"Closed"| N["ServiceManager.Start(identity)<br/>node.exe × entry 直启（ADR-024）"]
    M -->|"Healthy"| O["跳过拉起"]
    M -->|"Zombie"| P["SweepStaleServicePid → KillServiceProcess<br/>🔧修复点2 僵尸认领闭环"]
    M -->|"Foreign"| Q["快速失败 E2004"]
    N --> R["PollReadiness()<br/>TCP + HTTP 轮询（含日志宽限）"]
    O --> R
    P --> R
    R --> S{"就绪?"}
    S -->|"是"| T["UIInitialized → Running"]
    S -->|"否"| U["ReadinessTimedOut → ShuttingDown"]
```

**关键身份传递节点**：
- **I**: `RuntimeManager.EnsureRuntimeAsync` 产出 `DshRuntimeIdentity`（Source + node × 入口物理要件；ADR-024 唯一真相源）
- **K**: `ServiceManager.ProbePort()` 使用 `ShellLogic.ProcessManagement`（进程身份校验）
- **N**: 启动命令只能由 `Identity.NodeExePath × Identity.DshEntryJsPath` 拼装——旧 `start-dsh.vbs → cmd.exe` 轨道已废除（ADR-024 双轨制门禁守护）

---

## 3. 进程身份校验因果链

杀进程前的安全校验路径（防 PID 复用误杀）。

```mermaid
graph TD
    A["需要杀进程"] --> B["ShellLogic.ProcessManagement.GetProcessIdByPort()<br/>P/Invoke GetExtendedTcpTable"]
    B --> C{"PID > 0?"}
    C -->|"否"| D["端口已无占用者<br/>返回 true"]
    C -->|"是"| E["ShellLogic.ProcessManagement.IsLikelyDshService(pid)<br/>Process.GetProcessById → ProcessName == node?"]
    E --> F{"是 node 进程?"}
    F -->|"否"| G["拒绝 kill<br/>PID 复用防护"]
    F -->|"是"| H["FindPidListeningOn(port) == pid?<br/>端口归属校验"]
    H --> I{"端口归属正确?"}
    I -->|"否"| J["拒绝 kill<br/>可能 PID 复用"]
    I -->|"是"| K["ShellLogic.ProcessManagement.KillServiceProcess(pid, port)<br/>🔧修复点1: 等待taskkill退出 + 强杀确认1500ms + 重试一次"]
    K --> L["等待端口释放<br/>(最长 4s)"]
    L --> M{"端口释放?"}
    M -->|"是"| N["返回 true"]
    M -->|"否"| O["保留 PID 文件<br/>🔧修复点2: 下次启动 SweepStaleServicePid 认领"]
```

---

## 4. 配置降级因果链

lifetime 插件缺失时的安全回退路径。

```mermaid
graph TD
    A["ReadLifetimeMode()"] --> B["SafeReadText(SettingsPath)"]
    B --> C["ShellLogic.PluginConfig.IsLifetimePluginInstalled(DshHomeDir)"]
    C --> D{"插件存在?"}
    D -->|"否"| E["回退 FollowWindow<br/>+ 标记清理无效字段"]
    D -->|"是"| F["ShellLogic.RuntimeConfig.ParseLifetimeMode(json)"]
    F --> G{"值合法?"}
    G -->|"是"| H["保留用户选择"]
    G -->|"否"| I["回退 FollowWindow<br/>+ 标记清理"]
    E --> J["PurgeServiceLifetime()<br/>抹除无效字段"]
    I --> J
```

---

## 5. 启动崩溃检测因果链（ADR-023 多源主动拉取融合）

壳坐四个观察位 + CDP 精确采集，`BootHealthMonitor` 三态状态机（Pending/Healthy/Failed）融合判定，failed → 安全模式询问（每会话一次）→ ADR-022 两级阶梯。

```mermaid
graph TD
    A["EnsureServiceAndRuntime<br/>服务就绪"] --> B["StartBootHealthMonitor()<br/>BootSignatures = BootGuard.ResolveProfile(DSH_BOOT_SIGNATURES)"]
    B --> C["LogLoopAsync: 字节偏移增量扫签名表<br/>(IsShellAuthoredLogEntry 过滤壳自写行)"]
    B --> D["HttpLoopAsync: 轮询 Target.Url<br/>连续 ≥2 miss 才判死"]
    B --> E["AttachProcess(pid)<br/>Process.Exited 订阅; exit 0 忽略"]
    F["NavigationCompleted"] --> G["OnNavigationCompleted()<br/>grace 后按 interval 探针"]
    G --> H["ExecuteScriptOnMainWebAsync<br/>UI 线程 await（禁 GetResult 死锁）"]
    H --> I["EvaluatePageProbe<br/>解一层双重编码<br/>err坏签名一票 / 好符号 > DOM坏签名(降级Absent)"]
    I -->|"err BadSignature"| J["Report(Page, E2008)<br/>🔧修复点3"]
    I -->|"GoodSymbol"| K["MarkHealthy<br/>只停页面探针"]
    I -->|"DOM坏签名→Absent ×threshold"| J
    C -->|"命中"| L["Report(Log, E2003)"]
    D -->|"回死"| M["Report(Http, E2004)"]
    E -->|"非零退出"| N["Report(Process, E2007)"]
    L & J & M & N --> O["Failed 恰好一次<br/>Failed 事件"]
    P["CDP Runtime.exceptionThrown"] -->|"_earlyEvidence 缓冲<br/>或直接并入 verdict"| O
    O --> Q["PersistBootFailureEvidence<br/>safe-mode.json lastFailure (原子写)"]
    O --> R["ExportBootDiagnostics<br/>diagnostics/boot-failure-*.zip"]
    O --> S["AskEnterSafeModeOnce<br/>闸门1: TryConsumeSessionPrompt<br/>闸门2: DSH_TEST_SAFE_MODE_ANSWER / NoUiMode"]
    S -->|"yes"| T["RunSafeModeLadder<br/>Suspend() → Tier1 → Tier2 → ResumeAfterRestart"]
    S -->|"no + 曾激活"| U["🔧修复点4: Deactivate + 清DSH_PROFILE<br/>恢复正常启动路径（解粘滞）"]
    T -.->|"吸收态证据追加"| Q2["VerdictUpdated → 重写融合视图"]
```

### False Positives 陷阱（误报防护验收 Task 3）

| 陷阱 | 场景 | 拦截方式 |
|---|---|---|
| 探针异常 | 服务死亡瞬间 WebView 断连，ExecuteScriptAsync 抛错 | catch → Warn，异常轮不计缺席、绝不判 failed |
| 瞬时 HTTP 抖动 | 端口瞬断/代理抖动 | 连续 ≥2 次 miss 才判死；单次 miss 清零计数 |
| 壳主动重启窗口 | 安全模式 Tier 切换重启服务 | `Suspend()` 屏蔽全部判定并清零计数器，`ResumeAfterRestart` 重挂进程层 |
| 慢启动 | dsh 8s 后才就绪好符号 | grace_ms + absent_threshold 预算（S23：4s+8×1s=12s > 8s 零误报） |
| 旧日志误判 | 上次崩溃的日志残留 | 日志层从监控起点的字节偏移增量扫描，旧内容不参与判定 |
| 壳自写行误判 | E1008 插件崩溃捕获行含坏签名文本 | `IsShellAuthoredLogEntry`：JSON 行带 `"code":"E####"` 契约字段 → 跳过 |
| 好符号遮蔽真崩溃 | boot 标志先设、插件后崩 | 坏签名优先于好符号判定 |
| exit code 0 | 壳主动停止服务 | 进程层忽略 exit 0（降级 Warn） |

### 观察证据落点

- 统一日志：`[boot-monitor]` 前缀全层轨迹（armed/probe round/FAILED/HEALTHY/evidence appended）
- `safe-mode.json`：`lastFailure.{utc,code,summary,layers[]}`（四层融合视图，VerdictUpdated 重写）
- `diagnostics/boot-failure-*.zip`：log-warn.txt/state.txt/errors.txt
- 沙盒验收：`sandbox/verify-bootmonitor.ps1 S20–S24`

---

## 5. 2026-08 三处修复点（E2008 误判 / 僵尸清扫 / 安全模式粘滞）

经日志取证锁定的三处代码级根因与修复点（详见提交记录）：

| 修复点 | 现象 | 根因 | 修复 | 回归测试 |
|---|---|---|---|---|
| **修复点1** | 强杀后端口仍被占 / E2005 | `KillProcess` 仅发 taskkill 即轮询（未等 taskkill 退出），确认窗口仅 300ms 存在竞态 | 业务逻辑下沉为 `ShellLogic.ProcessManagement.KillServiceProcess(pid, port)`：`RunTaskKill` 封装（cmd 包装+重定向+超时 Kill 整树）等待 taskkill 退出；温和 `taskkill /T` → 800ms → 强杀 `taskkill /T /F` → 确认 1500ms → 重试一次；失败响亮上报 E2005 并保留 pid 文件 | `Regression_BootLifecycle.RealOs.RealOs_KillServiceProcess_TerminatesLiveListener_AndFreesPort`、`..._RefusesWrongPort_NoMisKill` |
| **修复点2** | 下次启动 E2005 端口占用（僵尸进程） | `SweepStaleServicePid` 缺失「活着且监听目标端口」认领分支（真僵尸直接落空） | `SweepStaleServicePid` 补该分支 → 调 `KillServiceProcess` 认领（内部 IsLikelyDshService + 端口归属双重防误杀）；`StopShellService` 端口释放超时后反查占用者按同样校验清理 | （认领构件即修复点1 的 RealOS 测试；`KillServiceProcess` 可靠终止真实监听进程即启动清扫闭环核心） |
| **修复点3** | E2008 误判（真实 UI 已渲染却被判死） | body.innerText 含字面量坏签名（如 "bootstrap facade is missing"）被一票判死；threshold 仅用于好符号缺席，无「实质内容已渲染」豁免 | `EvaluatePageProbe` 语义调整：err 原文坏签名仍一票 BadSignature（保留 S22 快速捕获）；`good` 符号 → GoodSymbol（渲染豁免）；仅 DOM 文本坏签名（且未确认渲染）→ 降级 Absent 携带 `dom-suspect[签名]=原文摘录`，由 `BootHealthMonitor` 按 AbsentThreshold 连续计次才判死 | `BootGuardContractTests.*DomSuspect*`、`BootHealthMonitorTests.PageLayer_DomBadSignature_*`、`PageLayer_ErrBadSignature_StillFailsImmediately` |
| **修复点4** | 安全模式粘滞（静默降级 .dsh-safe） | 用户答 "no" 后 `safe-mode.json`（tier=1, active）未解除，后续会话全部降级启动 | `AskAndMaybeEnterSafeMode` 用户答 "no" 且 `SafeMode.IsActive` 时执行 `Deactivate()` + 清除 `DSH_PROFILE` 环境变量 + Trace 记录，恢复正常启动路径 | `SafeModeStateTests.Activate_Then_Deactivate_PersistsRoundTrip` |

**关键决策权衡**：不重新引入 CTRL_BREAK（误杀用户 shell 风险 > 收益）；不改 `BootProfile` 默认 grace/threshold（只修判定语义）；不削弱 `scripts/test.ps1` 静态断言。

---

## 6. 2026-08-25 插件致崩 × 安全模式失灵（五处修复点）

> 事故：4 个第三方插件（dsh-launcher-lifetime / dsh-notification / dsh-web-search-anysearch /
> dsh-zh-guide）+ cordis.patch.yml 的 `id: web → searchProvider: anysearch` 补丁把 node 服务进程
> 搞崩（就绪后 ~0.5s exit=1）。BootHealthMonitor 三次会话全部判死成功并落盘，但安全模式
> 从未被询问——用户被迫手工改 package.json 与补丁才恢复。取证：`safe-mode.json` lastFailure、
> diagnostics/boot-failure-20260825-05*.zip ×4、dsh.log（三次 restart-service ask，零次 safe-mode ask）。

| # | 节点（§5 因果链） | 根因 | 修复 | 回归测试 |
|---|---|---|---|---|
| **修复点5** | Report(Process E2007) → HandleBootHealthFailed → 分类闸门 | `VerdictIndicatesPluginInvolvement` 只认页面层坏签名与插件 WebMessage；服务端崩溃无前端证据 ⇒ 永远路由"重启服务" | 归因通道扩展：日志层插件签名命中（`BootGuard.LogEvidenceIndicatesPlugin`）+ 第三方插件在场且进程层崩溃（`PluginConfig.ProfileHasThirdPartyBundles`）；路由决策下沉纯函数 `ShellLogic.BootRecoveryPolicy.Decide` | `BootFailureRoutingContractTests`、`Outcomes/BootFailureRoutingOutcomes` |
| **修复点6** | ServiceManager.Start（§2 服务启动链 N 的直启构件） | `using var p` 在 Start 返回即 Dispose 进程对象 → stdout/stderr 异步排空失效，服务输出从未落统一日志，日志层全程失明（连健康启动的 `dsh web:` 都消失） | 进程对象改静态追踪（`TrackServiceProcess`），下次启动替换时才释放旧对象；Dispose 不杀进程语义不变 | `Regression_ServiceOutputPipe.RealOs.RealOs_ServiceStdoutAndStderr_ArePipedToUnifiedLog` |
| **修复点7** | HandleBootHealthFailed 分支询问出口 | 匿名失败重复 N 次仍只问同一个无效的"重启吗"；事故形态是每次重开壳再崩，会话内闸门抓不住 | `SafeModeState.ConsecutiveBootFailures` 跨会话持久计数（Failed 恰好一次路径 `RegisterBootFailure` 推进，吸收态 VerdictUpdated 重写不推进；`HealthyDetected` 接线 `ResetFailureStreak` 复位）；连续 ≥3 次匿名失败升级安全模式询问 | `SafeModeStateTests.FailureStreak_*`、`Outcome_AnonymousCrashLoop_Escalates…` |
| **修复点8** | §3 进程终止链 KillServiceProcess 身份校验分支 | 已崩溃 pid 再次停止时 GetProcessById 抛异常被归因为 "not a dsh service process"（Warn+false），pid 文件滞留 | 区分"已消失无需杀"（Info+true，调用方走端口释放等待并清 pid 文件）与"活着但不是 node"（真防误杀拒绝不变） | `RealOs_KillServiceProcess_DeadPid_ReportsSuccess_NothingToKill`、`…_LiveNonNodeProcess_StillRefused` |
| **修复点9** | else 分支弹窗文案 | 无插件证据分支硬编码 E2008 文案（"页面启动自检未通过"），进程层崩溃也显示该文案误导排障 | headline 改为按裁决 ErrorCode 动态生成（`ErrorCodes.Describe(verdict.ErrorCode)`） | 弹窗文案属 UI 路径，由 TestHook E2E 覆盖（后续） |

**身份一致性检查**：修复点6 只改变进程对象生命周期，启动命令仍由
`Identity.NodeExePath × DshEntryJsPath` 经 `BuildArgs` 唯一拼装（ADR-024 不变）；
修复点7 的持久化文件仍是 safe-mode.json（原子写），安全模式激活/粘滞解除语义（修复点4）不变。

---

## 7. 2026-11 [E2007/E2008 误报根治]：配置等待态不判死 + 残留 pid attach 不判死

用户实测形态：未配置 API key 启动 → dsh 渲染出自己的欢迎/配置界面（boot 链未完成，
`__ModuleLoader__.mode` 不为 `"live"`）→ 页面层好符号持续缺席被误判 E2008；弹窗证据同时出现
`[Process] 进程 attach 失败（pid=4708 不存在）`（残留 pid 被当成进程崩溃证据），点"重启"后循环复发
（重启询问不受每会话一次闸门约束）。『页面其实是好的，只有真 failed 才该报错』。

| # | 节点（§5 因果链） | 根因 | 修复 | 回归测试 |
|---|---|---|---|---|
| **修复点10** | AttachProcess catch（进程层接线） | 残留/失效 pid 使 `RealProcessHandle` 构造（GetProcessById）抛错，却被 `Report(Process, E2007)` 判死整监控并弹窗——attach 是 best-effort 监视接线，不是崩溃裁决 | catch 仅 `Logger.Warn`，不再 Report（无错误码 → 无状态转移）；服务真死由 HTTP 层（E2004 连续 miss）与页面层（缺席阈值 E2008）兜底 | `BootHealthMonitorTests.ProcessLayer_AttachFactoryThrows_WarnsOnly_NeverFails`、`Regression_StalePidAttachTests.RealOs`、`Outcomes.ConfigurationWaitingState…` |
| **修复点11** | `EvaluatePageProbe` 缺席分支（页面层主触发器） | 好符号判定只认 boot 链完成（mode==="live"）；页面已渲染出 dsh 自身 UI（配置/欢迎界面）但 boot 链未完成 → 连续 `absent_threshold` 次缺席 → E2008 误报 | 新增 `Rendered` 分类：err 坏签名一票 / DOM 坏签名计票 / good 优先序不变，其后 `innerText ≥ RenderedMinTextChars(默认60，DSH_BOOT_SIGNATURES.rendered_min_text_chars 可覆盖)` 且无坏签名 → `Rendered` → Healthy；空白/纯加载页仍走缺席计票（慢启动保护不削弱） | `BootGuardContractTests.*Rendered*`、`BootHealthMonitorTests.PageLayer_RenderedContent_Healthy_ProbesStop`、`…_ShortTextBelowRenderedThreshold_StillAbsent…`、`Outcomes.ConfigWaitingStateOutcomes` |

**决策权衡**：渲染豁免采用代理特征（innerText 长度）而非精确 UI 断言——探针协议不变、向后兼容；
坏签名优先级置于豁免之上（err 一票 / DOM 计票），真崩溃错误 UI 不会被豁免；阈值默认 60 且可由
`DSH_BOOT_SIGNATURES` 校准，避免空页面被豁免。
**身份一致性检查**：修复点11 只调整探针求值语义，探针脚本形状（{good,text,err}）与
`BuildProbeScript` 不变；`HealthyDetected` 的 `ResetFailureStreak` 接线（修复点7）复用，配置等待态
计入"健康"不推进失败计数。修复点10 不触碰 `OnProcessExited` 的 Exited 事件路径（真崩溃 E2007 仍在）。

---

## 8. 2026-08-29 [token 栅栏]：dsh ≥0.1.2 根路径鉴权 → 壳 WebView 401 挂死（两处修复点）

实测形态（沙盒：官方源码构建的 0.1.2-alpha.1 SelfContained 运行时 × 真壳冷启动）：服务级链路全绿
（发现层选中新版、node 直启、tcp+http 6s 就绪、更新检查不误报），但启动横幅从 0.1.1 的
`dsh web: http://127.0.0.1:P` 变为 `dsh web: http://127.0.0.1:P/?token=…`（web-startup 新增根路径
token 信任栅栏，`--trusted-host` 只放宽 /api 不放宽根路径）。壳 WebView 导航裸 `Target.Url` →
401 错误页（E2004）→ 页面探针 `ExecuteScriptAsync` 永久挂起（派发后再无任何日志）→ 页面层瘫痪：
无 Failed 判定、无恢复询问、HTTP 层又"健康"故 update-guard 回滚也不触发——用户对着死窗口无解。
对照实验：0.1.1-rc.2 根路径 200（无 token），0.1.2-alpha.1 根路径 401。

| # | 节点（§2/§5 因果链） | 根因 | 修复 | 回归测试 |
|---|---|---|---|---|
| **修复点12** | 服务输出管道 → 壳 WebView 导航（§2 尾部） | 管道只把横幅渲染进统一日志，无人消费其中的 token URL；导航恒用裸 `Target.Url`；且 4 处服务重启后的 `Reload()` 停留旧地址（新进程新 token，Reload 必 401） | 新增纯函数 `ShellLogic.ServiceOutput.TryExtractTokenUrl`（宽进严出：http(s)+回环主机+端口匹配+token 非空，防伪造 stdout 劫持导航）；`ServiceManager` 管道逐行解析命中后经**静态**事件 `ServiceTokenUrlObserved` 上抛（identity/DSH_SERVICE_CMD 两条路径分属不同实例，通道必须类级；DSH_SERVICE_CMD 路径同时补齐管道对齐）；组合根记 `_serviceTokenUrl` 并在初始导航、安全模式/回滚/重启/apply 四处刷新点统一走 `NavigateMainWebToCurrentServiceUrl()`（导航替代 Reload） | `ServiceOutputContractTests`、`Regression_WebTokenAuthFence.RealOs`（真实 node 进程全链路） |
| **修复点13** | `PageLoopAsync` 探针轮（§5 页面层） | `await probe(script)` 无界——ExecuteScriptAsync 在 401 错误页永久不返回，单点挂起瘫痪整个页面层且无任何判定 | 每轮 `Task.WhenAny(probeTask, Task.Delay(pageProbeTimeout))`（默认 5s，ctor 可注入）；单次超时只 Warn 不判死（误报防护铁律不破）；**连续**超时达阈值（默认 10，ctor 可注入）→ E2008 判死（页面持续不可用的强证据）；任何成功往返清零连击；Stop() 经取消分支立即返回（挂起探针由 OnlyOnFaulted 续体吸收） | `BootHealthMonitorTests.PageProbe_HangingRounds_TimeoutStreakConvergesToFailed`、`…_TimeoutThenGoodSymbol_StreakReset_Healthy`、`…_HangingForever_StopReturnsPromptly_NoDeadlock` |

**决策权衡**：token 跟随选择"解析服务自报 URL"而非上游加关鉴权旗标——实测 `--trusted-host` 只作用于
/api 栅栏，且解析纯函数带回环+端口双校验后，伪造 stdout 无法把壳 WebView 引出本机。探针超时判死阈值
（10）高于缺席阈值（默认 5）——超时证据弱于缺席（可能只是渲染进程卡顿），宁可晚判不可误杀。
**身份一致性检查**：两修复点均不触碰发现层/身份比较（DshRuntimeIdentity 语义不变）；token URL 仅作
WebView 导航目标，`Target.Port`/就绪探测/健康监控仍用裸 `Target.Url`（401 对 tcp+http 就绪判定无影响，
与沙盒实测一致）。

---

## 如何使用本地图

### 修 Bug 流程

1. **定位节点**：在因果地图中找到 Bug 发生的节点
2. **检查上游**：上游传入的身份/数据是否正确？
3. **检查下游**：下游消费的身份/数据是否一致？
4. **验证不变量**：该节点的 `[INVARIANT]` 注释是否被违反？

### 新增测试流程

1. **确定因果链**：新功能属于哪条因果链？
2. **确定节点**：在因果链的哪个节点添加测试？
3. **选择测试类型**：
   - 节点级：单元测试（纯函数、可注入）
   - 链级：Outcome Contract 测试（跨越多模块，验证最终状态）

### 身份一致性检查

当修改涉及以下模块时，**必须**检查 `DshRuntimeIdentity` 的传递（ADR-024）：
- `DshDiscovery.DiscoverCurrentRuntime` — 统一发现（唯一合法 Identity 产出点）
- `UpdateChecker.ResolveLocalDshVersion` — 版本检测（必须委托 DshDiscovery，返回 `.Version`）
- `RuntimeManager.EnsureRuntimeAsync` — 身份解析（产出 RuntimeResolution.Identity）
- `ServiceManager.Start(identity, port)` — 服务启动（命令只由 Identity 拼装）
- `DshUpdateManager.HandlePendingAtStartup / ApplyPending` — 更新决策与应用（判定基于 `identity.Version`，应用后 `LogPostApplyIdentity` 取证）
