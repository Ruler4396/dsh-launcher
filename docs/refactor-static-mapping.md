# static 字段语义映射表（Program → WindowManager / WebViewManager / TrayManager）

> 本文档是 Task 4（`InitWebViewAsync` 迁入 `WebViewManager`）与 Task 5（生命周期编排迁出）的
> **先决交付物**，并随 Step 4/5 的 commit message 一起提交。
>
> 铁律：**static 字段语义必须逐字段声明新归属与生命周期（进程级 vs 每窗级）**，否则迁移会把
> 进程级节流错误降级为实例级（崩溃 10s/3 次节流变每个窗口各记各的 → 形同虚设），或把每窗级
> 状态错误提升为进程级（多弹窗互相污染 `_webviewRecoveryNeeded` → 白屏恢复错乱）。
>
> 行号基于 `Program.cs`（refactor/baseline 前的现状）。本表是**唯一权威**，任何迁移不得偏离。

## 图例

- **进程级**：进程内仅一份，不随窗口实例创建/销毁，生命周期 = 进程。多个窗口共享。
- **主窗级**：主窗口实例持有，弹窗各自独立，与主窗同生命周期。
- **环境持有者**：进程级单例，作为 WebView2 共享环境的所有者，**不随 manager 实例化**。

---

## A. WebView2 崩溃节流（进程级，归 WebViewManager，绝不降级）

| 字段 | 行号 | 类型 | 现语义 | 新归属 | 生命周期 | 迁移铁律 |
|---|---|---|---|---|---|---|
| `_crashCount` | 117 | `int` | 10s 窗口内连续渲染崩溃计数（P1-3 节流） | `WebViewManager` 实例 | **进程级** | 必须为进程级；若变实例级，每个弹窗各计各的，主窗达到 3 次上限后不再自愈，或弹窗把主窗的计数清零 |
| `_lastCrashTick` | 118 | `long` | 上次崩溃的 `Environment.TickCount64`（10s 窗口起点） | `WebViewManager` | **进程级** | 同上 |
| `_lastReloadTick` | 113 | `long` | 上次 Reload 的 tick（10s 重载节流，防崩溃死循环） | `WebViewManager` | **进程级** | 同上 |

> 映射表必须放入 Step 4 commit message 的重点：**这三个字段是进程级节流，跨所有 WebView2
> 实例（主窗 + 弹窗）共享**。实测事故（v0.3.4 血泪）——确定性崩溃页面若没有进程级节流，
> 会无限 崩→重载→崩 循环打满 CPU。任何"把 crash 字段放进每个 manager 实例"的重构都是回归。

---

## B. WebView2 恢复（主窗级，归 WebViewManager，但由托盘/窗口管线驱动）

| 字段 | 行号 | 类型 | 现语义 | 新归属 | 生命周期 | 迁移铁律 |
|---|---|---|---|---|---|---|
| `_mainWeb` | 126 | `WebView2` | 主窗口的 WebView2 控件引用（托盘恢复时检查/重载渲染） | `WebViewManager` | **主窗级** | 托盘恢复（ShowMainWindow）经 WindowManager 回调到 WebViewManager 读取；**不能**是静态进程级，否则多窗场景引用混乱 |
| `_webviewRecoveryNeeded` | 130 | `bool` | 渲染崩溃标志：窗口隐藏期间崩溃，恢复窗口时须重载页面，否则白屏 | `WebViewManager` | **主窗级** | 进程级崩溃事件会置此标志，但"谁需要恢复"是主窗属性；弹窗崩溃不得污染主窗标志（P1-4，`!ReferenceEquals(web,_mainWeb)` 已隔离） |
| `_hiddenSince` | 134 | `DateTime` | 上次隐藏窗口时间戳（长隐藏 >5min 渲染进程被回收 → 恢复时强制重载） | `WebViewManager` | **主窗级** | 由 FormClosing 隐藏路径写入，ShowMainWindow 读取并触发 `TryReloadWebViewDeferred` |

---

## C. 主题监听（进程级，归 TrayManager / WindowManager，进程退出释放）

| 字段 | 行号 | 类型 | 现语义 | 新归属 | 生命周期 | 迁移铁律 |
|---|---|---|---|---|---|---|
| `_themeTimer` | 121 | `Timer` | 主题轮询兜底（FSW 失效时定时重查） | `TrayManager` | **进程级** | 真实退出时 `ReleaseThemeWatcher` 释放；**FormClosing 拦截先于释放**（ORDER-INVARIANT） |
| `_themeWatcher` | 122 | `FileSystemWatcher` | settings.yaml 文件变化 → 即时切主题 | `TrayManager` | **进程级** | 同上 |
| `_themeEventsHandler` | 123 | `UserPreferenceChangedEventHandler` | 系统深/浅色切换事件 | `TrayManager` | **进程级** | 同上 |

---

## D. 服务生命周期（进程级，归 WindowManager / ServiceManager）

| 字段 | 行号 | 类型 | 现语义 | 新归属 | 生命周期 | 迁移铁律 |
|---|---|---|---|---|---|---|
| `_serviceStartedByShell` | 137 | `bool` | 本次会话是否由壳拉起服务（决定"跟随窗口/托盘退出"时是否停它） | `WindowManager` | **进程级** | 只影响主窗生命周期路径 |
| `_servicePid` | 140 | `int` | 壳托管服务监听 PID（内存缓存，关窗时免 netstat 卡顿） | `ServiceManager` | **进程级** | 见 `RecordServicePid`/`FindPidListeningOn` |

---

## E. 托盘（进程级，归 TrayManager）

| 字段 | 行号 | 类型 | 现语义 | 新归属 | 生命周期 | 迁移铁律 |
|---|---|---|---|---|---|---|
| `_trayIcon` | 143 | `NotifyIcon` | 托盘图标（按需显示，见 `EnsureTrayIcon`） | `TrayManager` | **进程级** | 退出路径 `Dispose`；托盘驻留隐藏路径**不** dispose（需保留唤起） |
| `_trayExitRequested` | 146 | `bool` | 托盘"退出"请求（放行 FormClosing 真关） | `TrayManager` | **进程级** | `ShouldInterceptCloseToTray(mode, trayExitRequested)` 决策纯函数依赖它 |

---

## F. 共享环境（环境持有者，进程级单例，不随 manager 实例化）

| 字段 | 行号 | 类型 | 现语义 | 新归属 | 生命周期 | 迁移铁律 |
|---|---|---|---|---|---|---|
| `_sharedEnvironment` | 298 | `CoreWebView2Environment` | 共享 WebView2 环境（主窗 + 弹窗共用 user-data 保持会话） | `WebViewManager`（**环境持有者**） | **进程级** | **唯一核心约束**：主窗 manager 单例、弹窗各自 new manager，但共享环境**不得**随 manager 实例化而重复创建（弹窗 new manager 时若重新 CreateAsync 会再开一份环境/锁）。`_sharedEnvironment`+`SemaphoreSlim` 保留为进程级环境持有者（可独立 static holder 类） |
| `SharedEnvLock` | 301 | `SemaphoreSlim` | 环境创建的互斥（并发弹窗 + 主窗同时初始化） | 同上（环境持有者） | **进程级** | 同上，保持 `(1,1)` |

---

## G. 图标缓存（进程级，GDI 句柄随进程退出释放）

| 字段 | 行号 | 类型 | 现语义 | 新归属 | 生命周期 | 迁移铁律 |
|---|---|---|---|---|---|---|
| `_darkWhaleIcon` | 2683 | `Icon` | 深色鲸鱼图标（浅色主题/任务栏用） | `TrayManager`/`WindowManager` | **进程级** | `Icon.FromHandle(GetHicon())` 的 GDI 句柄随进程退出释放；**不 dispose**（托盘驻留/主题切换复用时若 dispose 会悬挂句柄） |
| `_lightWhaleIcon` | 2686 | `Icon` | 白色鲸鱼图标（深色主题/托盘用） | 同上 | **进程级** | 同上 |
| `_blueWhaleIcon` | 2689 | `Icon` | 蓝色鲸鱼（任务栏/托盘固定） | `TrayManager` | **进程级** | `TrayWhaleIcon` 属性 `??=` 懒加载 |

---

## H. 更新提示（进程级，归独立 Update 编排，不在 Window/WebView manager 职责内）

| 字段 | 行号 | 类型 | 现语义 | 新归属 | 生命周期 | 迁移铁律 |
|---|---|---|---|---|---|---|
| `_pendingUpdate` | 954 | `PendingUpdate` | 待应用的 dsh 更新类型 | 保留在 Program 编排（或独立 `UpdateManager`） | **进程级** | 本轮重构不在 Window/WebView manager 内；保留即可 |
| `_pendingLatest` / `_pendingLocal` | 955 | `string` | 更新版本对 | 同上 | **进程级** | 同上 |
| `_pendingForm` | 956 | `Form` | 更新提示窗体 | 同上 | **进程级** | 同上 |

---

## 迁移自检（每 Step 后核对）

- [ ] 三个崩溃节流字段（A 组）仍是进程级，多弹窗共享。
- [ ] 共享环境（F 组）仍是进程级环境持有者，弹窗 new manager 不重建环境。
- [ ] 托盘隐藏路径仍写 `_hiddenSince`，托盘唤起仍读并触发恢复（B 组）。
- [ ] FormClosing 拦截托盘（`ShouldInterceptCloseToTray`）**先于** WebView2 销毁、先于主题释放。
- [ ] 托盘左键先 `SW_RESTORE` 再 `Activate`。
- [ ] 图标缓存不 dispose（G 组）。
