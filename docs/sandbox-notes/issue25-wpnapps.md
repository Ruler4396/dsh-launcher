# sandbox/issue25-wpnapps — Issue #25 崩溃复现与修复验证场景

> 遵循仓库核心约束六（单一沙盒根 `sandbox/`）。目标：**在不触碰宿主真实 dsh 的前提下**，
> 用最新仓库代码（DshWeb.exe Debug 构建）复现 issue #25 的 WPN（wpnapps.dll）崩溃，
> 并验证修复（Win10 WPN 崩溃护栏）后的行为。`.gitignore` 已忽略，禁止 `git add -A`。

## Issue #25 摘要

- 用户 Win10（wpnapps.dll **10.0.19041.7663**）+ v0.4.3.0 MSI：DshWeb.exe 启动后**约 30 秒必崩**
  （= 更新检查完成、系统 Toast 弹出时刻）。Application Error 1000：错误模块 `wpnapps.dll`、
  异常 `0xc0000005`、偏移固定 `0x60c3`；守护拉起后约 30 秒再崩 → 反复自愈循环。
- 崩溃模块是 Windows 10 推送通知平台（WPN）客户端；本仓库只有两条路径会让它在
  DshWeb.exe 进程内工作：① `SystemToast`（v0.4.2 手写 WinRT 互操作，更新/下载通知）；
  ② WebView2 web 通知（`WebViewPolicy` 自动放行 Notifications 权限 + dsh 前端通知插件）。

## 环境

| 项 | 值 |
|---|---|
| 本机 OS | Windows 10 22H2（10.0.19045） |
| 本机 wpnapps.dll | **10.0.19041.4522**（2022 年组件；reporter 为 19041.7663，2024-2025 更新推送） |
| DshWeb 代码 | 仓库 HEAD（SystemToast.cs 自 reporter 构建 55bfe533 起零改动） |
| 运行形态 | DshWeb.exe Debug 构建（本场景 `launcher/` 副本） |
| .NET | 10.0.303 SDK |

## 目录布局

```
launcher/    DshWeb.exe 构建产物副本（bin\Debug\net10.0-windows 整目录 robocopy）
home/        DSH_HOME 隔离（壳数据 + dsh.log 全落此处）
webview2/    DSH_WEBVIEW2_DATA 隔离（防与宿主实例互锁）
staging/     复现脚本 / 假 dsh 前端服务 / 截图 / PID 记录
global/      占位（本场景无需 npm 安装；假服务为 node 零依赖脚本）
```

## 复现隔离要点（与 notify-regression 同纪律）

- `DSH_SANDBOX=1`（禁机器级副作用）+ `DSH_HOME` 指向场景 home + `DSH_WEBVIEW2_DATA`
  指向场景 webview2 + `DSH_TELEMETRY_DISABLED=1`；
- **显式清空宿主注入的 `DSH_WEB_URL`/`DSH_SHELL`/`DSH_SESSION_ID`/`DSH_SESSION_JSONL`**；
- `DSH_WEB_URL=http://127.0.0.1:39884`（外部托管 + 假 dsh 前端服务：**壳绝不拉起/接管
  真实 dsh 服务**，mutex 按端口隔离与宿主 3080 零接触）或 `/notify`（WebView2 通知路径）；
- 清理只按记录 PID（staging/*.pid 的 taskkill /PID /T /F），绝不 `taskkill //IM`。

## 复现运行与结果（2026-09-07，均有 staging 日志/截图）

| 运行 | 触发路径 | 结果 | 证据 |
|---|---|---|---|
| signal（150s） | `DSH_TEST_UPDATE_SIGNAL=dsh:0.1.2-rc.1` + `DSH_TEST_INSTALL_MODE=msi` → NotifyPending → 系统 Toast（**带点击回调桥**，最贴近用户场景） | **不崩** | `toast step 4c: show ok` ×2、`update toast shown: dsh 有新版本`、进程存活 150s |
| webnotify（120s） | `DSH_WEB_URL=…/notify` → WebView2 自动放行通知 → 页面 `new Notification` → WPN toast | **不崩** | 进程存活 120s；日志含真实更新检查命中 + toast shown |
| toast（90s） | `DSH_TEST_TOAST=1` 自检（+该轮顺带真实更新检查命中弹 toast） | **不崩** | `toast step 4c: show ok`、`toast self-test: shown=True`、进程存活 90s；8s/50s 桌面截图像素对比有 toast 出现/消失差异（渲染路径真实执行） |

**结论**：与 reporter 完全相同的代码、三条 WPN 触发路径全部真实走通且真实渲染，
本机（wpnapps.dll 19041.4522）**不崩溃**——唯一无法在沙盒内模拟的变量是 WPN 组件
版本（系统组件禁止替换）。崩溃为固定偏移的确定性 native AV，高度指向较新
wpnapps.dll（19041.7663）自身的 toast 路径缺陷。

## 修复（见仓库源码与 CHANGELOG）

- `ShellLogic.ToastPolicy.ShouldUseSystemToast(major, minor, build)`（纯函数）：
  Win10（build<22000）→ false → `SystemToast.TryShow` 直接降级（Warn 留痕 + return false）→
  调用方走既有 **托盘气泡→标题驻留** 回退链；
- `ShellLogic.WebViewPolicy.IsAutoGrantedPermission(kind, osBuild)`：Win10 不再自动放行
  **Notifications**（堵 WebView2 web 通知 → WPN 的宿主内 toast 路径；页面侧
  `Notification.permission=denied` 可感知）；
- 测试钩子 `DSH_TEST_FORCE_TOAST=1` 可强制越过护栏（Win11/CI 冒烟真 WPN 通路，与
  既有 `DSH_TEST_FORCE_TOAST_FAIL` 对称）；
- 回归：`Regression_Issue25_WpnToastGuard.RealOs`（零 Mock）——真实拉起 DshWeb.exe
  （外部托管假服务），Win10 断言 suppress 留痕 + `toast self-test: shown=False` +
  无任何 `toast step` 轨迹 + 进程存活满 60s；Win11 断言放行 + 存活。

## 修复后验证（本机 Win10）

- 运行 `staging/run-issue25.ps1 -Mode toast`：期望 dsh.log 出现
  `system toast suppressed on Windows 10 (WPN crash guard, issue #25)`、
  `toast self-test: shown=False`、**无** `toast step` 轨迹、进程存活满观察窗；
- `DSH_TEST_FORCE_TOAST=1` 对照：护栏被强制越过，恢复 `toast step … show ok` 轨迹。

## 遗留项（建议）

向 reporter 索要：崩溃进程完整模块列表、`--diagnose` 包、WER LocalDumps 崩溃转储
（`HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\DshWeb.exe`），
用 dump 确认 wpnapps.dll 内 0x60c3 处的调用栈，把"版本差异推断"升级为"行级根因"；
并请其验证修复构建在 Win10 上不再循环崩溃。