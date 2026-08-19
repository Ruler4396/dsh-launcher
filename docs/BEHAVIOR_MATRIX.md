# BEHAVIOR_MATRIX.md — 重构行为验收矩阵

> 目的：把 DshShellForm / InitWebViewAsync / Program.Main 的**每条可观察行为**建立
> "行为 × 验证方式"矩阵，作为每个 Step commit 的验收依据。任一行为被迁丢（漏订阅、改序、
> 改生命周期）都必须在对应验证点暴露。
>
> 验证方式图例：
> - **单测**：`dotnet test`（纯函数/决策函数，无 GUI）
> - **e2e**：`scripts/e2e-test.ps1`（真实 GUI 探针，Windows 桌面会话）
> - **test.ps1**：静态断言（源码文本特征，防回归到旧结构）
> - **冒烟**：Task 3 的真机冒烟矩阵（人工，Step 3/4/5/6 必跑）
>
> 编号规则：`G` 几何、`F` F11、`W` WebView2、`L` 生命周期。每 Step 验收 = 该 Step 涉及编号全绿。

---

## G 几何类（DshShellForm / WindowManager，Step 1/3 重点）

| # | 行为 | 触发 | 迁移落点 | 单测 | e2e | test.ps1 | 冒烟 |
|---|---|---|---|---|---|---|---|
| G1 | 最大化 0px 铺满工作区、不遮任务栏（WS_CAPTION 移除 + WM_GETMINMAXINFO 设 rcWork） | 双击标题栏/系统菜单最大化 | `ComputeMaximizedMinMaxInfo` 纯函数 + WM_GETMINMAXINFO 适配器 | ✅ 异构 DPI/副屏单测 | ✅ `WM_SYSCOMMAND SC_MAXIMIZE` 后 GetWindowRect == rcWork ≤2px | 完成态断言 WndProc 不在 Program | ✅ 最大化/还原 0px 铺满 |
| G2 | 焦点切换无经典标题栏闪影（WM_NCACTIVATE 吞掉返回 1） | Alt+Tab / 点击 | `WndProc` WM_NCACTIVATE 分支 | ✅ `ShouldConsumeNcActivate` | ✅ Alt+Tab 循环后窗口自绘标题栏仍存在 | — | ✅ Alt+Tab×20 无经典标题栏 |
| G3 | 最大化/还原无经典边框闪影（`_lastWindowState` 门控 ForceNonClientRedraw） | 最大化/还原/OnShown | OnResize + ForceNonClientRedraw | — | ✅ 标题栏子控件存在可见（防按钮消失） | — | ✅ 最大化/还原无边框闪影 |
| G4 | 8px 边缘缩放，负坐标副屏不抛异常（LParam 64 位拆位） | 拖窗口边缘 | `HitTestResizeEdge` 纯函数 + `ShellLogic.SplitLParam` | ✅ 左侧负坐标副屏 | ✅ 最大化时边缘无缩放指针 | — | ✅ 拖拽缩放平滑无闪烁 |
| G5 | 最大化时边缘不出现缩放指针（`WindowState != Maximized` 门控） | 最大化状态拖边缘 | `HitTestResizeEdge` 返回 null | ✅ 最大化返回 null | — | — | ✅ 最大化时边缘无缩放指针 |
| G6 | Aero Snap / Win+方向键 / Alt+Space / 任务栏收起（CreateParams 样式位） | 拖顶/系统菜单 | `CreateParams` WS_THICKFRAME\|MINIMIZEBOX\|MAXIMIZEBOX\|SYSMENU | ✅ `ComputeCreateParamsStyle` | — | 完成态断言 CreateParams 不在 Program | ✅ 拖顶最大化 / Win+←→ / Alt+Space |
| G7 | 1px 边框与 LayoutChrome 布局（标题栏 + WebView2 内缩） | 窗口尺寸变化 | `LayoutChromeRects` 纯函数 + LayoutChrome | ✅ `LayoutChromeRects` 单测 | ✅ 标题栏高度 ≈32×DPI | — | ✅ 布局无错位 |
| G8 | 跨 DPI 移动重算（DpiChanged → Rescale + LayoutChrome） | 跨异构 DPI 显示器移动 | DpiChanged 接线 + LayoutChrome | ✅ 150%/100% 异构 DPI 单测 | — | — | ✅ 跨异构 DPI 移动后布局正确且最大化不丢窗 |
| G9 | 窗口位置/大小/最大化状态记忆恢复（WindowStateStore） | 退出→重启 | WindowManager 生命周期 + `ShellLogic.RestoreWindowPosition` | ✅ 越界钳制单测 | ✅ E4 现有断言 | — | ✅ 多屏恢复 |
| G10 | 多屏/DPI 窗口丢失修复：物理像素工作区（MonitorFromWindow + GetMonitorInfo）替代 `Screen.FromHandle` 逻辑像素陷阱 | 副屏拔掉/150% 屏 | `ComputeMaximizedMinMaxInfo` 入参物理像素 | ✅ 左侧负坐标副屏、上下堆叠、150%/100% 异构 | ✅ 最大化==rcWork | — | ✅ 跨异构 DPI 移动后最大化不丢窗 |

## F F11 类（WindowManager，Step 2 重点）

| # | 行为 | 触发 | 迁移落点 | 单测 | e2e | test.ps1 | 冒烟 |
|---|---|---|---|---|---|---|---|
| F1 | 物理 F11 切换最大化/还原且吞键（WH_KEYBOARD_LL 系统级钩子） | 物理/注入 F11 | `F11LowLevelHook` + `ShouldHandleF11Hook` | ✅ 现有 F11HookDecisionTests | ✅ keybd_event 注入 VK_F11 后窗口状态翻转（分层门禁：hard 必过 / CI soft 可 SKIP；SendInput 因 .NET INPUT 布局被拒 error 87 已弃用） | — | ✅ 物理 F11 |
| F2 | 仅主窗前台生效（`GetForegroundWindow()==form.Handle`） | 非前台按 F11 | F11 钩子 `isForeground` 闭包（缓存 `var hwnd=form.Handle`） | ✅ 非前台不处理单测 | — | — | ✅ 其他程序 F11 不抢 |
| F3 | 钩子随窗体销毁（Dispose） | 关窗 | F11 钩子 Dispose 时机 | — | ✅ 关窗后进程退出 | — | ✅ 钩子随窗体销毁 |
| F4 | 跨线程修复：创建钩子前 UI 线程缓存 `var hwnd = form.Handle`，lambda 比对缓存值 | 窗体销毁期 F11 竞态 | WindowManager 构造 | ✅ 销毁期无 ObjectDisposedException 竞态 | — | — | ✅ Alt+Tab×20 无异常 |

## W WebView2 类（WebViewManager，Step 4 重点，最高风险）

| # | 行为 | 触发 | 迁移落点 | 单测 | e2e | test.ps1 | 冒烟 |
|---|---|---|---|---|---|---|---|
| W1 | 权限自动授权白名单（`IsAutoGrantedPermission`） | PermissionRequested | `WebViewManager.InitializeAsync` 事件接线 | ✅ 现有 SecurityBoundaryTests | — | 完成态断言 WebView2 事件接线不在 Program | ✅ 弹窗开闭 |
| W2 | 弹窗分类：外部浏览器 / 壳内弹窗（`ClassifyPopup`） | NewWindowRequested | 同上 | ✅ 现有 SecurityBoundaryTests | — | 同上 | ✅ 弹窗开闭 |
| W3 | 下载名推导 + 安全打开白名单（`IsSafeToOpen`，.exe/.html 不自动开） | DownloadStarting | 同上 | ✅ 现有 SecurityBoundaryTests | — | 同上 | ✅ 下载 .txt 自动开 / .exe 仅提示 |
| W4 | 导航白名单（S3：仅 127.0.0.1/localhost，外部转浏览器） | NavigationStarting | 同上 | ✅ 现有 SecurityBoundaryTests | — | 同上 | ✅ 外部链接转浏览器 |
| W5 | 渲染崩溃 10s/3 次节流（E1007）——**进程级** | ProcessFailed | `WebViewManager`（崩溃字段进程级，见映射表 A 组） | ✅ 崩溃节流决策纯函数单测 | — | — | ✅ 确定性崩溃页面不无限崩 |
| W6 | 托盘隐藏期崩溃 → 恢复重载（`TryReloadWebViewDeferred`） | 隐藏→唤起 | WebViewManager + WindowManager 回调 | — | ✅ 白屏断言（DSH_WEBVIEW2_READYSTATE） | — | ✅ 关闭→托盘→唤起内容非白屏 |
| W7 | 长隐藏(>5min)恢复重载 | 隐藏 >5min→唤起 | 同上（`_hiddenSince`） | ✅ 长隐藏判定纯函数 | — | — | ✅ 长隐藏恢复非白屏 |
| W8 | 共享环境进程级持有（`_sharedEnvironment`+SemaphoreSlim 环境持有者，不随 manager 实例化重建） | 主窗+弹窗并发初始化 | 环境持有者类 | ✅ 环境单例语义单测 | — | — | ✅ 弹窗开闭不锁死 |

## L 生命周期类（WindowManager / Program.Main 编排，Step 5/6 重点）

| # | 行为 | 触发 | 迁移落点 | 单测 | e2e | test.ps1 | 冒烟 |
|---|---|---|---|---|---|---|---|
| L1 | **FormClosing 托盘拦截先于 WebView2 销毁**（0.1.10 血泪：拦截先于 Dispose，否则托盘唤起白屏） | 关窗（托盘驻留模式） | `ShouldInterceptCloseToTray(mode, trayExitRequested)` 决策 + FormClosing 顺序 | ✅ 决策纯函数单测 | ✅ 关闭→托盘→唤起非白屏 | 完成态断言 + ORDER-INVARIANT 注释 | ✅ 关闭→托盘→唤起内容非白屏 |
| L2 | 托盘左键先 `SW_RESTORE` 再 `Activate`（最小化后 Activate 无效） | 托盘左键/菜单唤起 | `ShowMainWindow` | — | — | — | ✅ 托盘左键唤起 |
| L3 | 单实例聚焦（Mutex + FindWindow 标题，第二次启动 restore+foreground） | 二次启动 | Program.Main 编排 | — | ✅ 现有 E3（second instance exits） | — | ✅ 二次启动聚焦已有窗 |
| L4 | 窗口位置+最大化状态记忆（SaveWindowState / Load） | 退出→重启 | WindowManager + WindowStateStore | ✅ 越界钳制单测 | ✅ 现有 E4 | — | ✅ 位置/最大化记忆 |
| L5 | 主题即时切换（DWM 沉浸式 + FSW + 系统事件） | 系统/文件主题变化 | TrayManager.RegisterThemeWatcher | — | — | — | ✅ 深浅主题即时切换 |
| L6 | lifetime 三模式关窗语义（常驻0/托盘驻留1/跟随窗口2） | 关窗 | `ResolveEffectiveLifetime` + FormClosing | ✅ 现有 ShellLogicTests | ✅ E3 alive=False（跟随窗口） | — | ✅ 三模式关窗 |
| L7 | 事件接线"顺序即语义"：HandleCreated 先于主题应用；订阅次数恰一次（防双订阅/漏订阅静默丢失） | 启动 | WindowManager 构造"接线自检" Debug 断言 | ✅ 接线次数断言 | — | — | ✅ 启动无双重载/双主题 |

---

## 静默丢失风险点（每条靠哪条检测兜底）

1. **崩溃节流降级为实例级**（W5）：最易被迁丢。兜底 = static 映射表 A 组 + 单测（崩溃节流决策纯函数）+ 冒烟确定性崩溃页。
2. **FormClosing 拦截晚于 WebView2 销毁**（L1）：0.1.10 血泪。兜底 = `ShouldInterceptCloseToTray` 单测 + e2e 白屏断言 + 冒烟关闭→托盘→唤起。
3. **事件接线漏订阅/双订阅**（L7）：无报错的静默丢失（如导航白名单忘挂 → 任意外部链接可入，不崩溃）。兜底 = WindowManager"接线自检" Debug 断言 + `DSH_TRACE_WIRING=1` 冒烟 trace + 完成态静态断言。
4. **托盘唤起先 Activate 后 Restore**（L2）：最小化后点不回来。兜底 = 冒烟托盘左键唤起 + `DSH_TRACE_WIRING`。
5. **共享环境随弹窗 manager 重复创建**（W8）：弹窗 new manager 若重建环境 → user-data 锁死/多份环境。兜底 = 环境持有者类 + 单测 + 冒烟弹窗开闭。
6. **`_hiddenSince`/`_webviewRecoveryNeeded` 被提为进程级**（W6/W7）：多弹窗污染主窗恢复。兜底 = 映射表 B 组 + 冒烟托盘恢复非白屏。

---

## 每 Step 验收门禁（真机冒烟任一失败即停，`git bisect refactor/baseline..HEAD` 定位）

- **Step 1**（纯函数+多屏修复）：G1/G4/G5/G7/G8/G10 单测全绿 + 多屏冒烟。
- **Step 2**（F11 迁入）：F1/F2/F3/F4 单测全绿 + 物理 F11 冒烟。
- **Step 3**（DshShellForm 薄壳）：G1-G10 全绿 + 冒烟门禁 1（最大化铺满/无闪影/边缘缩放/跨 DPI）。
- **Step 4**（WebView 迁入）：W1-W8 全绿 + 冒烟门禁 2（弹窗/下载/崩溃节流/托盘恢复非白屏）。
- **Step 5**（生命周期迁出）：L1-L7 全绿 + 冒烟门禁 3（托盘唤起/单实例/主题/三模式）。
- **Step 6**（收尾）：静态断言全启 + e2e 全量 + 冒烟全量。

---

## 门禁定义（Phase 1 起强制执行）

**CI e2e-geo 闭环**（`scripts/e2e-test.ps1` E3b 段）：
- G1/G3/G7/W6 **必须全绿**；
- F1 分层门禁（Q1）：`GEO_F11_MODE=hard`（本地/真机）必须 `f11=up:True down:False`；
  `GEO_F11_MODE=soft`（CI e2e-geo job env）失败重试 2 次仍败 → 输出 `f11=SKIP(soft)`，**不计 FAIL**。
- 禁用 `WM_SYSCOMMAND SC_MAXIMIZE` 替代 F1（那是 G1 语义，测的不是 F11 钩子）。

**每 commit 硬门**：
1. `dotnet build` 0 err；
2. `dotnet test` 全绿；
3. `scripts/test.ps1` 全绿；
4. 本地 geo 探针 **hard** 5 断言全绿——跑前必须 fresh publish（`dotnet publish` 到临时目录），
   **禁用旧 dist zip**（e2e 的 -PublishDir 模式已强制优先 PublishDir exe 并 WARN 旧 zip）。

**真机门**：Step 3/4/5/6 跑 Task 3 冒烟矩阵，任一失败即停（`git bisect refactor/baseline..HEAD`）。

**闭环判定**：CI e2e-geo = G1/G3/G7/W6 全绿 且 (F1 绿 或 f11=SKIP(soft))。达闭环才进 Step 1；
G1/G3/G7/W6 红 → 先修基础设施，不进 Step 1。

### e2e/探针模态硬化（Q1 派生）
- 壳 `--ui-probe` 或环境 `DSH_E2E=1` 时，`ShowError` 只写日志 + stdout，**不弹模态**。
- 根治：探针路径上壳误判服务不可用弹 E2004 模态 + 探针 WaitMain 30s → "看似卡死"。
- 正常 GUI（无 `--ui-probe`/`DSH_E2E`）不受影响。
