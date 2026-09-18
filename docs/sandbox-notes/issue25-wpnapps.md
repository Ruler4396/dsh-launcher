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
| 本机 wpnapps.dll | **10.0.19041.4522**（2022 年组件；reporter #1 为 19041.7663，2024-2025 更新推送） |
| reporter #2 环境 | Windows 11 25H2（10.0.26200.9457）+ wpnapps.dll **10.0.26100.9278**，偏移 `0x53fb`，2026-09-18 单日 12 组 Event 1000+1026 全同签名（WER bucket 1839361804423943695）——**推翻初版"Win11 不受影响"假设** |
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

> 这条路修过三次，前两版都是错的：45048989 做成"仅 Win10（build<22000）降级"，前提
> "本机 19041.4522 与 Win11 均不触发"被 reporter #2 的 Win11 25H2 报告否证；第二版改成
> "默认全平台关闭 + `DSH_ENABLE_SYSTEM_TOAST` opt-in"，仍然是**保留通路只关开关**——
> 开关可以被越过，用户"只想把通知调回来"就会重新进崩溃循环。
> 现在这版不再加护栏，**直接拆掉通路**。

- **删除** `Windows/SystemToast.cs`（手写 combase/WinRT 互操作、`Activated` 事件桥、
  未打包 AUMID 的 `HKCU\Classes\AppUserModelId` 注册）与 `ShellLogic.ToastPolicy`
  （`BuildToastXml`/`ToastAumid`/`ShouldUseSystemToast`），`DSH_ENABLE_SYSTEM_TOAST` 与
  `DSH_TEST_FORCE_TOAST*` 一并移除 → **wpnapps.dll 在本进程永不加载**；
- 通知统一为 `Windows/NoticeCard.cs`（唯一实现）：自绘、非模态（`ShowWithoutActivation`
  不抢焦点）、置顶、贴工作区右下角、可选点击动作 + × 关闭、到时自动收起；全局单实例 +
  有界队列（8 条，溢出丢最旧并 Warn）。几何只来自纯函数 `ShellLogic.NoticeCardLayout`，
  绘制侧不再自乘 DPI 系数；套用 `ImeContextGuard`；
- 三档回退链（Toast → 托盘气泡 → 标题驻留）收敛为一条，`WindowManager.ShowBalloonTip`
  删除；标题栏 `（有更新）` 作为**状态指示**保留并改成幂等；
- 六个通知点全部改接卡片：安全更新/新版本、下载完成、更新待应用、更新已就绪、构建失败、
  安全模式启动。安全模式那条**自带"点击退出安全模式并重启"动作**——`ExitSafeModeRequested`
  原本只挂在 toast 的 `onClick` 上，标题栏"（安全模式）"只是文字，通知没有可点动作就等于
  用户没有 UI 途径离开降级态；
- 网页通知（HTML `Notification` API）**不由壳代管**：实测 `@deepseek-ai/dsh\lib` 里
  `new Notification(`/`showNotification(`/`Notification.requestPermission` **全 0 命中**，
  dsh 本体不发系统通知；第三方插件是否使用无法穷证 → 不为不确定的通路维护第二套呈现。
  `WebViewPolicy` 的 Notifications 权限恢复一律放行（拒权限不是这个崩溃的防护手段，
  拿它当防护等于白砍插件功能）；
- 新自检通道 `DSH_TEST_NOTICE_CARD=1` 替代 `DSH_TEST_TOAST`：启动即真实呈现一张卡片，
  留痕 `notice card self-test: presented=…`。

## 修复后验证

- 通知走通：`DSH_TEST_NOTICE_CARD=1` 拉起 → dsh.log 出现
  `notice card self-test: presented=True`，屏幕右下角出现卡片、不抢焦点、到时自己收起；
- **崩溃面归零（核心判据）**：在通知已呈现的那一刻枚举 DshWeb.exe 已加载模块，
  **不得出现 `wpnapps.dll`**（`Regression_Issue25_WpnToastGuard.RealOs` 的 A 用例）；
  验证窗口内 `%LOCALAPPDATA%\CrashDumps\` 不得出现新的 `DshWeb.exe.*.dmp`；
- 无头也能跑的两条闸门（CI）：`DshWeb.dll` 元数据不含
  `Windows.UI.Notifications`/`wpnapps`/`CreateToastNotifier`/`ToastNotificationManager`；
  源码不含 WPN 成员引用与 `ShowBalloonTip`（`scripts/test.ps1`）；
- `signal` 模式（`DSH_TEST_UPDATE_SIGNAL` + `DSH_TEST_INSTALL_MODE=msi`）：应看到
  `update notice presented: dsh 有新版本 …` 且标题栏出现 `（有更新）`；**不得**再出现
  `update toast shown`（reporter 实测的死亡前最后一行）；
- `webnotify` 模式：`Notification.permission` 仍为 **granted**；插件通知由 WebView2 原生
  渲染，壳不插手。

## 遗留项（建议）

1. **插件网页通知不由壳代管，且"代管"在原理上做不到全覆盖**——两条独立理由，都已核实：
   - `CoreWebView2.NotificationReceived` 官方定义是 **"for non-persistent notifications"**
     （`ReportClicked` 同样限定 non-persistent）。也就是说它只覆盖窗口内 `new Notification()`；
     Service Worker 的 `showNotification()` 属于**持久通知**，不经这个事件，仍由 Chromium
     自己经 WPN 渲染。置 `Handled=true` 挡不住这一半。
   - dsh 插件跑在 **dsh 的 node 服务进程**里（`~/.dsh/profiles/*`，由 ServiceManager 拉起），
     不在 WebView2 沙箱内。它在服务端自己发通知（自带 WPN 调用、PowerShell、任何 npm 包）
     根本不经过壳，壳无从接管。
   结论：**通知无法收敛成一条链路**，所以不做接管、也不拒权限（拒权限只会把"能用的功能"
   换成"看不见的功能"，防护价值为零）。实测数据：本机 Win10 上放行权限后
   `Notification.permission=granted`、`new Notification()` 构造成功并在屏幕上出现系统通知；
   而宿主 `DshWeb.exe` 与全部 6 个 `msedgewebview2.exe` 进程**均未加载 wpnapps.dll**
   （有界探针：只扫这两类 PID、硬墙钟上限）。"网页通知到底在哪个进程触碰 WPN"仍未钉死，
   但它落在浏览器进程的话最坏现象是 `BrowserProcessExited`——而
   `WebViewManager.ProcessFailed` 目前只处理 Render/Gpu 三类，**BrowserProcessExited 无人接**，
   现象是永久白屏无自愈。**这条才是值得补的**（与 issue #25 不同的独立缺陷）。
2. 向 reporter 索要 WER LocalDumps 崩溃转储（`HKLM\...\LocalDumps\DshWeb.exe`），确认
   `0x60c3`（Win10）/ `0x53fb`（Win11）处调用栈。通路已删除，行级根因不再是修复前提，
   但对上游反馈与"确认我们拆的是不是唯一入口"仍有价值。
3. 卡片的已知限制：只保留最新一条可见（历史进队列，溢出丢最旧），错过就靠标题栏标记兜；
   点卡片即触发动作并收起，没有"稍后再说"按钮。若 bot 消息类通知将来需要成堆可见，
   要加的是通知中心，不是第二条通道。
4. 观感并存（用户实测看到）：壳通知 = 右下角白底自绘卡片、不进通知中心、自动收起；
   插件网页通知 = 左下角 OS 深色 toast、进通知中心。两套外观/两个角落是"不接管"的既定代价，
   不是重复提示（内容不同）。旧版在 Win10 上因拒权限看不到前者、在 Win11 上两者同为系统 toast。