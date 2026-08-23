# 系统因果地图（System Causal Map）

> **铁律**：修复任何跨模块 Bug 时，Agent **必须**先在本地图中定位 Bug 发生的节点，
> 并检查上下游的身份传递（`DshRuntimeIdentity`）是否一致。

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
        M --> N["⑩ StartDshServiceViaVbs()"]
        N --> O["⑪ WaitServiceReady()"]
        O -->|"📦 证据: HTTP 响应"| P["⑫ DshDiscovery.DiscoverCurrentRuntime()"]
        P --> Q{"InstalledVersion<br/>== targetVersion?"}
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
- **① → ③**: `Identity.InstalledVersion` 决定"是否有更新"
- **① → ⑧**: `Identity.Source` 决定"用什么命令安装"
- **⑩**: `Identity.InvocationCommand` 决定"用什么命令启动服务"
- **⑫**: **终极验证** — Identity 必须等于目标版本（防 FP1）

**L3 测试保护**：
- `Outcome_Update_Changes_Actual_Running_Identity` — 验证 ⑫ 的 Identity 变化
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
    M -->|"Closed"| N["StartService()<br/>wscript start-dsh.vbs"]
    M -->|"Healthy"| O["跳过拉起"]
    M -->|"Zombie"| P["SweepStaleServicePid → KillServiceProcess<br/>🔧修复点2 僵尸认领闭环"]
    M -->|"Foreign"| Q["快速失败 E2004"]
    N --> R["WaitReadyAsync()<br/>TCP + HTTP 轮询"]
    O --> R
    P --> R
    R --> S{"就绪?"}
    S -->|"是"| T["UIInitialized → Running"]
    S -->|"否"| U["ReadinessTimedOut → ShuttingDown"]
```

**关键身份传递节点**：
- **I**: `RuntimeResolver.ResolveExisting()` 返回 `NodeEnvironment`（Node.js 身份）
- **K**: `ServiceManager.ProbePort()` 使用 `ShellLogic.ProcessManagement`（进程身份校验）
- **N**: `start-dsh.vbs` 内部的 dsh 发现链（`where dsh` → npm shim → npx）

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

当修改涉及以下模块时，**必须**检查 `DshRuntimeIdentity` 的传递：
- `UpdateChecker` — 版本检测
- `start-dsh.vbs` / `Program.StartDshServiceViaVbs` — 服务启动
- `DshDiscovery` — 统一发现
- `Program.ReadGlobalDshVersion` — 版本读取
- `Program.HandlePendingUpdateAtStartup` — 更新决策
