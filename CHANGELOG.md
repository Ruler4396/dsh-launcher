# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.3.1] - Unreleased

> v0.3.0 规划中 P2 储备的六项全部落地（commit 27881f8，单测 140/140）：WebView2 缺失自动修复、MSI 安装时 winget 自动装 .NET、SIGINT 优雅终止、Node 默认 LTS 升级、日志超长告警、镜像路由纯函数化。

### 新增

- **WebView2 缺失兜底（自动修复）**：WebView2 初始化失败时，先静默安装 Evergreen Bootstrapper（官方固定链接下载约 2MB → `/silent /install` → 重试初始化），仍失败才弹 E1006；不再一上来就让用户手动装 WebView2。
- **MSI 前置检查 winget 自动装 .NET**：`PrereqCheck.exe` 缺 .NET 时弹「自动安装(A)」，一键 `winget install Microsoft.DotNet.DesktopRuntime.10 --silent --accept-package-agreements --accept-source-agreements`（10 分钟超时），装完重测满足即继续安装；winget 缺失回退下载页；仅缺 Node 时不显示自动安装按钮。
- **常驻超长日志告警**：启动早段检测到 `dsh.log` >50MB 且最后写入 >24h → 记 `Warn`（轮转留给下次重启——热轮转会被运行中 node 的句柄阻止，故只告警不折腾）。
- **启动状态窗"取消"真正生效（质量治理）**：此前点"取消"只关窗，后台任务继续跑、UI 最长假死 180s；现在取消即真正终止等待流程（commit a052b50）。
- **进程终止前身份校验（质量治理）**：PID 复用防护——只杀 node 服务进程，壳与卸载 CA 两处都校验；强杀后确认，杀不干净则保留 pid 文件供下次启动认领。
- **渲染进程连续崩溃上限（质量治理）**：10s 窗口内连续崩溃 3 次 → 停止自动重载并记 E1007，保留托盘唤窗手动恢复（不再死循环重载）。
- **插件弹窗崩溃不再污染主窗口恢复标志（质量治理）**：弹窗崩溃不再误触主窗口的崩溃恢复标记。
- **"已下载待应用"更新启动时气泡提示一次（质量治理）**：服务健康跳过应用、或应用失败时都不再静默，附手动 `npm` 命令供用户自行处理。
- **错误码 E1007 + 测试与 CI 纳入（质量治理）**：新增 E1007（渲染进程反复崩溃）；CI 纳入 `scripts/test.ps1`（无 `-Smoke`）作为 PR gate；新增错误码契约单元测试（R02）。

### 变更

- **Node 便携默认 LTS 升级**：`v22.16.0` → `v24.15.0`（2026-08 核对：Node 24.x Active LTS，支持至 2028-04，最大化支持窗口）；`DSH_NODE_VERSION` 仍可覆盖。
- **SIGINT 尽力而为优雅终止**：停服务先 `TryGracefulStop`（`AttachConsole` + `CTRL_BREAK`，node 映射 SIGBREAK 可选清理），无控制台进程时自动降级温和 `taskkill`，仍不退才 `/f`（等待窗 1.5s）——替代此前直白 taskkill。
- **崩溃自愈策略调整（质量治理，与其在"新增"的崩溃上限条目合并）**：从"反复自动重载"改为"连续崩溃达阈值即停止、保留手动恢复"；GitHub/npm 更新检测与服务/就绪判定等超时整体放宽（如更新检测 8s→15s，弱网不再误报"无更新"）。

### 修复

- **便携 Node 镜像路由去重**：`BaseUrls` 重构为纯函数（`DSH_NODE_MIRROR` → 上次成功源 → nodejs.org → npmmirror，`Distinct` 去重），消除返回链重复的可能，新增 4 个单元测试。
- **错误日志级别失真（质量治理）**：用户取消/拒绝被记为 Error 污染错误汇总、E4001 双写 → `ShowError` 支持级别参数与去重。
- **E1006 兜底失败无诊断日志（质量治理）**：WebView2 静默安装兜底改为区分下载/安装/超时三阶段记录。
- **删除死码（质量治理）**：移除废弃错误码 E1001/E3001 与 `ShellLogic.ResolveLogPath`；`ReadLogTail` 改流式读取（大日志不整读）。
- **WebView2 共享环境互斥（质量治理）**：共享环境创建加锁，消除并发弹窗的创建竞态。
- **主题监听资源释放（质量治理）**：真实退出时释放 SystemEvents/FSW/轮询 Timer，消除关窗后毫秒级竞态。
- **托盘创建失败不静默（质量治理）**：创建失败记 `Warn` 日志。
- **配置降级精确判定（质量治理）**：只在**顶层** `serviceLifetime` 键命中时触发，消除子串误报；插件在但值越界也清理。
- **更新检测超时放宽（质量治理）**：GitHub/npm 更新检测超时 8s→15s，弱网不再误报"无更新"。
- **--diagnose 脱敏增强（质量治理）**：额外替换 `%USERPROFILE%`、`~\`、`\用户名\` 常见路径片段，减少路径泄漏。

## [0.3.0] - Unreleased

> 底座重构版本：可观测性统一（一份日志、一套错误码、一键诊断）＋更省心的环境与生命周期（便携 Node、延迟更新、托盘按需）＋更强的稳健性（僵尸清理、窗口容灾）＋更清楚的数据边界。重构规划与验收见 [docs/v0.3.0-plan.md](docs/v0.3.0-plan.md)。

### 新增

- **一键诊断导出**：`DshWeb.exe --diagnose [--min-level warn|error]` 把统一日志（可按级别过滤）、环境变量、node/dotnet/webview2 版本、错误码汇总打成**脱敏** zip（用户目录替换为 `%USER%`）放到"下载"文件夹，便于无脑汇报——绝不含 `.credentials.yaml` / 会话 / 存储 / 插件内容。
- **Node.js 便携自动补齐**：检测不到 Node.js 时弹一次性确认框，自动下载 LTS 便携版到 `%LOCALAPPDATA%\dsh-launcher\env\node\`（SHA256 校验 + 镜像回退 nodejs.org → npmmirror，`DSH_NODE_VERSION` / `DSH_NODE_MIRROR` 可覆盖），只改进程级 PATH、不改系统环境变量与注册表。
- **窗口位置记忆与多显示器容灾**：窗口位置/大小持久化到 `window-state.json`；副屏拔掉等越界时回退主屏工作区居中并钳制，任务栏变化时整格钳制进工作区。

### 变更

- **统一日志**：旧 `shell.log` 与 `%USERPROFILE%\.dsh-web*.log` 的多文件方案收敛为**单一文件** `DSH_HOME\dsh-launcher\dsh.log`（默认 `~/.dsh\dsh-launcher\dsh.log`）——壳写 JSON Lines（级别 Info/Warn/Error，`DSH_LOG_LEVEL` 控制最小级别），dsh 服务输出经 `start-dsh.vbs` 追加同文件共存；轮转由壳独家负责（>30MB 或 >3 天 → `.1/.2`，保留 ≤3 份）。旧日志路径不再产生新文件。
- **错误码**：所有用户可见错误弹窗带 `[E####]` 码（目录见 `src/DshShell/ErrorCodes.cs`：E1001 未检测到 Node、E2002 服务启动超时、E2011 插件缺失配置降级等），与结构化日志的 `code` 字段、诊断导出的错误码汇总共用同一套码，消息可 Ctrl+C 复制。
- **托盘按需显示**：默认隐藏；仅当检测到 dsh-launcher-lifetime 插件已安装、或本会话有待通知的更新时才显示托盘。
- **dsh 非侵入式更新（延迟应用）**：点更新气泡 → 确认 → 后台 `npm pack` 下载到 `DataDir\staging`（不碰运行中的环境）→ 下次启动拉起服务前自动应用（`npm install -g` 固定版本，写入 `pending-update.json`）；失败不阻塞。
- **配置自动回退（插件降级）**：dsh-launcher-lifetime 插件卸载后，壳自动忽略并抹除 `settings.json` 里残留的 `serviceLifetime`（回退"跟随窗口"），无需手动删 JSON。
- **僵尸进程清理 + 孤儿健康校验**：启动时清理上次崩溃遗留的僵尸 Node 进程（只动 pid 文件记录的 PID、绝不按进程名批量杀）；孤儿服务健康（HTTP 就绪）才接管复用，坏状态则清理并重建。
- **卸载清理数据边界**：MSI 卸载自动清理 `DSH_HOME\dsh-launcher\`（自身配置/统一日志/窗口状态等）与旧 `%USERPROFILE%\.dsh-web*.log`；便携版 `uninstall-autostart.cmd -CleanData`（显式可选）同边界清理。**绝不触碰** `profiles/`、`settings.yaml`、`.credentials.yaml`、sessions、插件等 dsh 生态数据。

### 移除

- 移除了旧的 `shell.log` 与 `%USERPROFILE%\.dsh-web*.log` 日志文件方案（统一为 `dsh.log`）；停用 `start-dsh.vbs` 的自行截断/轮转（所有权归壳）。

### 说明

- 被拒/降级方案见 [docs/v0.3.0-plan.md](docs/v0.3.0-plan.md)：主题 accent 增强（dsh 无法读取自定义主题色）、镜像延迟测速、运行时静默装 .NET（技术不可能，改 MSI 链路 winget，P2 储备）、SIGINT 优雅终止（降级 P2）、自制下载管线（不建，npm 当下载器）。

## [0.2.5] - 2026-08-15

### 变更

- **自启改为"拉壳"方案**：HKCU Run 的 dsh-launcher 值从 `wscript ...start-dsh.vbs`（静默起服务）改为直接指向 **`DshWeb.exe`**——登录 → 壳窗口出现 → 壳自行探测/拉起 dsh 服务（未运行→跑 start-dsh.vbs + 状态窗，已运行→收养）。壳全程管理服务生命周期，自启不再依赖独立的 vbs 静默服务路径；旧版 wscript+vbs 格式的存量 Run 值会被壳首启自动迁移为新格式

### 修复

- **勾选"开机自启"后重启不自启（0.2.5 发版前实测发现）**：两级落地要求壳首次启动补写 HKCU Run，但用户自然流程是"装完勾选→直接重启"，壳从未运行，HKCU Run 永远不落地。修复：安装 CA 同时写 HKCU Run（UAC 提权下 msiexec 服务进程以发起用户身份运行，写真实用户 hive 可靠），壳首启自愈保留作兜底。
- **勾选"开机自启"后标志不落地（0.2.4 发版后实测发现）**：0.2.4 使用 MSI Feature Level 条件控制自启标志组件安装，但 MSI 在修改安装场景下对 Absent feature 的 Level 条件不重新评估——实测 AUTO_START_OPTION=1 已设置但 Feature Request 仍为 Null。尝试改用 Component 条件同样失效（条件已写入 MSI 但组件仍被无条件安装）。最终改为 immediate 自定义动作 `SetAutoStartFlag` 直接写 HKLM 注册表值，绕过组件/Feature 条件机制，所有场景（全新安装/修改安装/升级安装/修复）一致可靠。

## [0.2.4] - 2026-08-15

### 修复

- **安装向导"开机自启"默认显示为勾选但实际未启用（UI 与实际不一致）**：MSI CheckBox 控件对存在非空值的属性（哪怕 "0"）会渲染为勾选状态，而 feature 条件层面 "0" 又不装组件——用户看到勾上了、实际没自启，很可能也是上游 issue 报告者的遗误诱因。修复：默认不勾的 checkbox 属性必须无默认值（属性不存在 → 不勾；勾选 → "1"；取消 → 空串），与 WiX 官方 FAQ 推荐做法一致。
- **per-machine 安装勾选"开机自启"后 HKCU Run 值不落地、登录不自启（issue 实测报告）**：根因是 per-machine 提权安装中 `RegistryValue Root="HKCU"` 写入不可靠（值落到提升上下文或被静默丢弃，所有用户 hive 均扫不到）。改为两级落地：MSI 勾选时只写机器级意图标志（`HKLM\Software\dsh-launcher\AutoStartWanted=1`，可靠、随卸载自动清除），壳首次启动时读到标志后以当前用户身份补写 `HKCU\...\Run`——用户上下文写 HKCU 100% 可靠，交互/静默安装均覆盖；也顺带解决"其他管理员过 UAC 时自启写错 hive"的问题（谁先用壳，自启就落在谁头上）。升级/自定义目录导致路径变化时自动更新。
- **卸载时清理 HKCU Run 自启值**：per-machine 卸载同样无法用注册表组件可靠删 HKCU（对称问题），改由 immediate 自定义动作（发起用户上下文）删除，只删内容包含 `start-dsh.vbs` 的同名值，失败不阻断卸载。
- **卸载时 HKLM 意图标志残留（Level 条件在卸载时重评的隐蔽行为）**：MSI 对 Level=0（条件禁用）feature 的组件在卸载时不请求移除——实测组件 Installed: Local 但 Request: Null，注册表值残留。修复：卸载 CA 兜底同时删除 HKCU Run 与 HKLM 标志，调度条件加 `NOT UPGRADINGPRODUCTCODE`（升级链路不触发，用户已启用的自启跨版本保留）。
- **`uninstall-autostart.cmd` 同时清除 HKLM 意图标志**（需管理员）：防止壳自愈机制在下次启动时重新创建用户刚手动删掉的自启项。

## [0.2.3] - 2026-08-15

### 修复

- **窗口贴边（Aero Snap）失效（0.1.10 自绘标题栏引入的回归）**：拖到屏幕边缘半屏/拖顶最大化/Win+方向键全部恢复。根因是 `FormBorderStyle.None` 剥掉了 `WS_CAPTION|WS_THICKFRAME` 样式位；改为加回样式位 + `WM_NCCALCSIZE` 吃掉原生框架预留（Chromium / Windows Terminal 同款方案），自绘标题栏与 1px 边框观感不变，附带恢复 Win11 原生圆角/阴影/最小化动画与 Alt+Space 系统菜单。
- **托盘右键菜单点击其他位置不消失**：菜单窗从未被激活过则永远收不到失活消息（根因），弹出时显式 `Activate()` 抢占激活，点击任意其他窗口/桌面即关闭；Esc 关闭保持；关闭时顺手释放淡入 Timer 与菜单字体。

### 调整

- **托盘菜单"退出"字重再降一档**：Medium(500)/伪粗体双画 → Regular(400) 单画（Noto Sans SC → DengXian → Microsoft YaHei 回退链不变），与 1.8px 图标描边视觉平衡。

## [0.2.2] - 2026-08-15

### 修复

- **MSI 安装失败（0.2.1 撤回原因，严重）**：0.2.1 加入的 .NET Runtime 检测（RegistrySearch + LaunchCondition）因 **WiX 5.0.2 的 AppSearch/Signature 表缺陷**（AppSearch 表引用 `Signature` 表但该表条目缺失）导致检测属性恒为空 → 条件恒假 → **任何机器（即使已装 .NET 10）安装都报"需要 .NET Desktop Runtime 10"并中止（1603）**。0.2.2 移除该方案
- **安装前置检查改为独立检测程序**：新增 `PrereqCheck.exe`（Type-38 外部 exe，与文件夹选择器同模式）在向导启动时（InstallUISequence 最前）检测 **.NET Desktop Runtime 10**（shared 目录 10.x 存在性）与 **Node.js 18+**（PATH 可执行 + 注册表兜底）；任一缺失 → **弹窗列出缺失项并提供"去下载"按钮**（打开 .NET 官方下载页 / nodejs.org），缺失或取消即中止安装（`Return="check"`）；**弹窗 60 秒无响应自动按"否"中止**（兜底静默/无人值守场景不挂起）；升级/修复/卸载不拦截（`NOT Installed` 条件）
- **安装前置检测不影响正常安装**：环境满足时静默通过（实测安装/升级/卸载全部 exit=0）

### 变更

- **安装前置检查更完整**：除 .NET Runtime 外同时检测 Node.js（dsh 服务运行必需），引导下载对应正确版本

## [0.2.1] - 2026-08-15（已撤回）

> **注意**：0.2.1 因上述 MSI 安装失败问题已从 GitHub 撤回，请勿使用该版本；请用 0.2.2。

### 变更

- **MSI 安装向导前置检查 .NET Desktop Runtime 10**：缺失时安装前明确提示（附 `winget install Microsoft.DotNet.DesktopRuntime.10` 指引），不再出现"装完双击无反应"；检测 WOW6432Node 视图下 `sharedfx\Microsoft.WindowsDesktop.App` 的 10.* 版本值（SDK 自带 runtime 与独立安装器均会写该键）
- **托盘菜单字体回退链**：Noto Sans SC Medium（思源黑体 500，原生加粗）→ DengXian（等线）→ Microsoft YaHei UI → 系统默认；等线/雅黑无中间字重时伪粗体双画补粗，缺字体静默降级
- **MSI 目录选择器回写校验**：文件夹选择结果带一次性随机令牌（Guid），安装动作校验令牌匹配且路径为本地绝对路径、拒绝系统目录（Windows/Program Files/ProgramData 等）后才采纳，防低权限攻击者预置伪造路径
- **下载完成智能打开**：仅无害扩展名（图片/文本/pdf/音视频/压缩包等）自动用默认程序打开；其余（.html/.svg/.hta/.exe 等可执行代码面）落盘后托盘气泡提示，不自动执行
- **主窗口导航白名单**：只允许本地（127.0.0.1/localhost）导航，外部 http(s) 导航自动转系统默认浏览器——壳无地址栏，防被重定向到伪站点
- **CI 供应链加固**：第三方 action 全部 pin commit SHA；顶层 `permissions: contents: read`（仅 Release 步骤放开 write）；tag 名经环境变量注入不再内插进脚本
- **start-dsh.vbs 日志重定向加引号**：用户目录含空格/元字符时不再截断日志路径或注入命令（实测复现修复）

### 修复

- **副屏负坐标窗口边缘缩放失效**（B1）：WM_NCHITTEST 的 64 位 lParam 用 `ToInt32()` 在左侧/上方副屏抛 OverflowException，改为有符号 16 位拆位
- **单实例误聚焦插件弹窗**（B2）：弹窗初始标题不再与主窗口同名（"dsh-launcher 弹窗"），第二实例按标题找主窗口时不会误聚焦 popup
- **托盘菜单淡入 Timer 泄漏**（B3）：动画完成后 Dispose（每次弹菜单一个，不再等 GC）
- **托盘菜单位置屏幕外**：屏幕边界自适应——左/上越界翻转到鼠标另一侧，仍越界贴工作区边缘（左侧竖排任务栏时菜单不再被推出屏幕）
- **托盘菜单透明不显示**：`CreateCompatibleDC`/`SelectObject`/`DeleteDC`/`DeleteObject` 四个 P/Invoke 的 DLL 归属修正为 gdi32.dll（此前误标 user32.dll 导致渲染异常被吞、菜单全透明）
- **卸载后 ProgramData 空目录残留**：壳启动时清理中转文件与空目录（非空则不动，不删第三方文件）
- **MSI 自定义动作失败静默**：浏览/回写动作 `Return="ignore"` → `Return="check"`（失败即中止安装，不再悄悄继续）

### 其他

- `.gitattributes` 统一脚本/源码行尾（*.vbs/*.cmd/*.cs 等 CRLF），本地与 CI 构建产物校验和可跨环境复现
- 安全策略文档补充已知边界说明（PATH 解析、HKCU 自启归属）

## [0.2.0] - 2026-08-15

### 变更

- **托盘右键菜单自绘重构**：LayeredWindow 位图渲染（`UpdateLayeredWindow`，alpha 平滑圆角无锯齿）+ **16px 大圆角** + 内容垂直居中；**仅保留"退出"**一项（删除"显示 / 隐藏窗口"，窗口显示用左键单击托盘置顶）；红色电源图标（GraphicsPath 矢量绘制）+ 黑色"退出"文字，hover 淡红圆角（内缩同心）、弹出淡入动画（120ms）、点击外部/Esc 关闭
- **托盘菜单尺寸按 DPI 缩放**：物理像素 = 逻辑尺寸 × scale（DPI/96），150% 缩放屏上与 HTML 预览观感一致（此前按 96dpi 设计，高 DPI 屏上菜单/字体显小、间距被压缩像"遮挡"）
- **托盘菜单字体回退链**：Noto Sans SC Medium（思源黑体 500，原生"加粗一点点"）→ DengXian（等线，Win10/11 自带）→ Microsoft YaHei UI → 系统默认；等线/雅黑无中间字重时用伪粗体双画（Regular 字形 x+1 偏移，介于 Regular/Bold 之间），缺字体静默降级不崩
- **更新推送策略**：dsh-launcher 自身**普通更新不推送**，只有标记为**安全/重要更新**（GitHub Release body 含 `SECURITY` 或 tag 含 `-sec`）才托盘气泡提示（点击打开 Releases 下载页，气泡驻留 25s）；dsh（npm）有新版本仍提示（一键更新）
- **MSI 安装目录"浏览"按钮 → 现代化文件夹选择器**（Windows 10/11 新版文件夹对话框，IFileDialog）：Type-38 外部 exe（客户端进程弹窗，`FolderPicker.exe`）→ 所选路径写 `C:\ProgramData\dsh-launcher\picked.txt` → **DTF Type-1 托管 CA**（`WixToolset.Dtf.CustomAction` 5.0.2，net20 匹配 SfxCA 的 CLR 2.0，在 msiexec CA server 执行但其 `MsiSetProperty` 回写会同步回客户端 UI——实测日志 `PROPERTY CHANGE: Modifying INSTALLFOLDER`）→ 写安装目录属性。**输入框回显用双对话框交替**（ChooseFolderDlg ↔ ChooseFolderDlg2：MSI 控件静态绑定、属性变化不重绘，NewDialog 重建对话框后 PathEdit 重读属性）。关键坑：① SfxCA 选 stub 看 `$(Platform)`（默认 x86 → x64 msiexec 加载 193，需 `<Platform>x64</Platform>`）；② SfxCA 绑 CLR 2.0（net48 程序集 BadImageFormat，需 net20 目标）；③ `SetTargetPath` 参数必须展开成**属性名**（`[WIXUI_INSTALLDIR]`），字面路径报 MSI 2872；④ 取消按钮必须 `EndDialog Exit`（`Return` 在主 UI 序列会被当作正常结束 → 取消也被安装）
- **托盘/任务栏/资源管理器图标 → DeepSeek 蓝鲸鱼**（#4D6BFE，深浅背景都清晰）：托盘、任务栏按钮（WM_SETICON）、exe 图标（app.ico，文件夹/程序功能/快捷方式/固定）统一蓝色；**自绘标题栏鲸鱼保持主题**（深色→白、浅色→深）
- **自动检测并更新 dsh**：启动后异步检查 `@deepseek-ai/dsh`（npm registry）最新版，有新版本时**托盘气泡**提示，点击气泡确认后一键执行 `npm install -g @deepseek-ai/dsh@latest`（完成提示，需重启壳生效）；网络失败/无新版静默不打扰
- **版本更新检测接口**（`UpdateChecker`，已接入上述托盘气泡流程）：GitHub Releases（dsh-launcher 自身）+ npm registry（dsh）版本比较，语义化版本比较含单测；GitHub API 匿名限流、失败静默

### 修复

- **跟随窗口模式下关闭窗口服务不停（issue）**：`StopShellService` 的强制杀（`taskkill /f`）原先在后台 Task 里延迟 1.5s 执行——温和 `taskkill` 对无窗口的 node（wscript 隐藏启动）发 WM_CLOSE 无效，而壳退出后后台 Task 未及执行 `/f`，服务残留、端口仍监听。修复：温和终止 → **同步短等待（限时 &lt;1s）** → 未停则**在壳退出前同步强制 `/f`**，实测关窗即停、不卡关窗
- **托盘菜单透明不显示（0.1.32–0.1.34）**：重写时把 `CreateCompatibleDC`/`SelectObject`/`DeleteDC`/`DeleteObject` 四个 P/Invoke 误标为 `user32.dll`（实为 **gdi32.dll**）→ 每次渲染抛 `EntryPointNotFoundException` 被 catch 吞掉，LayeredWindow 位图永不生效、窗口全透明。修复 DLL 归属后实测渲染正常（日志 + 像素级验证）
- **托盘菜单位置被推出屏幕**：位置按"鼠标左上方"计算（`pt.X - 宽 + 12`），左侧竖排任务栏（托盘图标贴左边缘）时菜单直接越出屏幕。修复：屏幕边界自适应——左/上越界翻转到鼠标另一侧，仍越界贴工作区边缘
- **MSI 安装向导点"取消"/关窗口仍会完成安装**：自定义对话框的取消按钮误用 `EndDialog Return`——主 UI 序列（非模态）中 `Return` 被 MSI 当作"正常结束 UI（IDOK）"，安装继续执行；`Exit` 才是"用户取消退出安装"。所有自定义对话框（选项页、两份目录页）取消按钮改为 `EndDialog Exit`（欢迎页等 WiX 标准对话框本就是 Exit，故"上一步回欢迎页再取消"不装）
- **MSI 安装页"开机自启"说明文字被裁切**：复选框高度只有一行但文案两行（"…内存占用相对较大，非必要不推荐开启"）导致上下文字被遮挡——复选框调高为两行高度并显式换行，下方控件同步下移
- **服务停留模式每次打开被重置为跟随窗口**：根因是 profile 里安装的 dsh-launcher-lifetime 插件为**旧版**（`apply` 无条件把设置写回默认）——之前的同步因 PowerShell `Copy-Item 目录到已存在目录` 会**嵌套复制**（`lib\lib`）而从未真正覆盖旧文件；已清理嵌套目录并正确同步修复版（插件"文件已存在不覆盖用户选择"），hash 校验一致
- **系统任务栏图标在浅色主题下变黑**：Windows 11 任务栏按钮读取的是窗口小图标（ICON_SMALL），此前它跟随主题（浅色 → 深色鲸鱼），浅色主题下任务栏 logo 就变成黑色——修复：小图标固定鲸鱼（后随图标统一改为 DeepSeek 蓝，任何主题、深浅背景都清晰）；exe 资源图标（app.ico）同步更新

## [0.1.10] - 2026-08-14

### 变更

- **自绘标题栏（无边框窗口）**：彻底解决"主题切换后标题栏不刷新"（实测本机 DWM 属性切换后标题栏画面只有焦点变化才重绘，SWP_FRAMECHANGED / RedrawWindow / WM_NCPAINT 等全部无效）——像浏览器一样自己画标题栏：主题切换 = 改自绘颜色，**即时生效**。标题栏含主题鲸鱼图标、标题、MDL2 字形窗口按钮（最小化/最大化还原/关闭，带 hover 效果、关闭红色）、拖拽移动、双击最大化、右键系统菜单、边缘 8px 缩放、最大化限制在工作区（不遮任务栏）
- **标题栏 DPI 自适应**：标题栏高度/按钮/图标按 `DeviceDpi` 缩放（125%/150%/200% 都协调）；跨 DPI 显示器移动窗口（`DpiChanged`）自动重算布局；四周 1px 高对比边框替代阴影提升质感（带 WebView2 的无边框窗口无法获得系统阴影——WebView2 是不透明子窗口，与透明边距/扩展帧方案冲突）
- **配置位置迁移到 dsh 主目录**：settings.json / 启动轨迹日志 / 服务 PID 记录从 `%LOCALAPPDATA%\dsh-launcher` 移到 **`DSH_HOME\dsh-launcher`**（默认 `~/.dsh`，与 dsh 生态一致——dsh 自己的设置如 settings.yaml 也在 DSH_HOME；不散落在 LOCALAPPDATA，清理/迁移时跟着 dsh 走）。启动时自动迁移旧数据（保留设置值）并清理旧目录，卸载后无残留；WebView2 用户数据保持在 `%LOCALAPPDATA%\DshWeb`（浏览器标准位置，会话登录态随壳走）
- **主题以用户的选择为主**：壳读取 dsh 设置页的主题选择（`DSH_HOME/settings.yaml` 的 `ui-theme.preference`，dark/light/system），而不是只跟随系统；深色 → 白色鲸鱼 + 深色标题栏，浅色 → 深色鲸鱼 + 浅色标题栏，切换实时生效（FileSystemWatcher 即时 + 500ms 轮询兜底）
- **托盘交互优化**：**左键单击 = 窗口置顶显示**（开着就提到最上层并聚焦，最小化先还原，不会误关窗口）；**右键 = 只弹菜单**（不动窗口）；"显示 / 隐藏窗口"保留在右键菜单里供手动隐藏
- **托盘菜单样式 v2**：Win11 风格圆角浮层 + MDL2 图标（眼睛=显示/隐藏、电源=退出）+ 文字垂直居中（离屏渲染像素级验证图标与文字中心差 0px）+ 内容自适应宽度
- **双色小鲸鱼图标**：托盘图标与**系统任务栏图标固定白色鲸鱼**（深色背景看不清深色鲸鱼）；窗口标题栏小图标跟随主题（深色 → 白色鲸鱼，浅色 → 深色鲸鱼）
- **插件改名**：配套插件设置页"服务停留模式"更名为 **"Node 服务驻留"**（文案点明 node 服务与托盘的关系）；侧边栏导航图标不再用默认齿轮（本机定制了 dsh 包的 `navIcon` 映射，dsh 升级后需重新应用，见插件 README）
- README 增加"与 dsh 插件联动"章节（安装命令、设置文件位置、模式切换入口说明）

### 修复

- **自绘标题栏遮挡内容**：Dock 布局下 WebView2 从 y=0 填充盖住标题栏区域，内容顶部被挡——改为手动 Bounds + Anchor（内容从标题栏下方开始、四边跟随窗口缩放），实测窗口尺寸任意调整布局正常
- **最小化后托盘单击"点不回来"**：最小化窗口忽略 `Activate()`——单击托盘先 `SW_RESTORE` 还原再激活
- **深色模式下窗口/任务栏鲸鱼"消失"**：主题与图标配色逻辑写反（深色主题误用深色鲸鱼）——深色主题用白色鲸鱼、浅色用深色鲸鱼
- **主题解析误读其他配置段**：`ui-theme.preference` 的解析严格限定在 ui-theme 段内，不再误读其他段的同名字段
- **关闭窗口卡 1-2 秒**：① 不再显式 Dispose WebView2（进程退出后浏览器子进程自动清理）；② 停服务改为异步 + 内存缓存服务 PID（关窗不再跑 netstat）——实测关窗到进程退出约 100ms
- **服务模式"常驻"被重置**：设置读取兼容旧路径（`%LOCALAPPDATA%`，旧插件写入位置）——新位置读不到时回退旧值并迁移；插件侧同时修复"每次启动无条件写回默认"（见插件 CHANGELOG）
- **托盘唤起后立即重载页面可能崩溃**（实测 0xc0000005 / .NET Runtime internal error）：隐藏→恢复→立即 `Reload()` 与 WebView2 的可见性处理存在竞态；改为延迟 500ms 且窗口再次隐藏时放弃并留待下次恢复
- **崩溃后服务残留无人管理**：壳拉起的服务 PID 记录到数据目录；下次启动若发现该 PID 仍在监听，自动接管（"跟随窗口"关窗时一并停掉），避免进程崩溃后 node 服务永久残留占内存
- **插件每次启动把服务模式重置为默认**：插件 `apply` 只在无显式配置且文件缺失时写默认值，用户通过设置页的选择不再被覆盖

## [0.1.9] - 2026-08-14

### 变更

- **托盘菜单样式打磨**：去掉系统默认样式的"老气感"（左侧图标留白、跟随深色主题变黑、系统主题色 hover）——改为简洁白底 + 1px 浅灰边框 + 浅灰 hover + 深色文字，字体微软雅黑 9pt，菜单项留白舒展；保留"显示 / 隐藏窗口"与"退出"两项
- **默认省内存**：未配置服务模式时默认"跟随窗口"（关窗即停 dsh 服务，下次启动自动拉起；想常驻在插件设置里改）；MSI 安装向导的**开机自启默认不勾选**，勾选框注明"内存占用相对较大，非必要不推荐开启"
- **托盘图标始终显示**（任何服务模式）：此前只在"托盘驻留"模式创建，导致默认"常驻"模式下用户找不到"服务模式"切换入口；现在启动即有托盘（小鲸鱼），右键可随时切换模式/退出（常驻模式托盘退出只退壳、服务保留）；托盘创建失败不影响壳主流程
- 托盘右键菜单瘦身：移除"服务模式"子菜单（改为插件在 Harness 设置页配置），保留显示/隐藏与退出
- 壳支持环境变量 `DSH_WEB_PORT` 指定**壳托管的服务端口**（3080 被占用时可用；`DSH_WEB_URL` 仍为外部托管语义）：壳按该端口拉起 dsh 服务（start-dsh.vbs 支持 `DSH_PORT` 透传），单实例锁、就绪探测、关窗停服务都按该端口
- 启动轨迹日志：`%LOCALAPPDATA%\dsh-launcher\shell.log` 记录壳的关键决策点（单实例、端口探测、服务拉起、就绪判定、窗口显示），启动异常时可直接查看定位

### 修复

- **从托盘唤起窗口一片空白**：
  1. **托盘驻留模式点关闭按钮必然白屏**：`FormClosing` 先销毁 WebView2（`web.Dispose()`）再判断是否拦截到托盘——拦截后窗口虽隐藏、控件却已销毁，唤起时只剩空白。修复：托盘驻留拦截判断移到 WebView2 销毁之前（拦截时保留控件，真正退出时才销毁），已实测"关闭→托盘→唤起"内容正常
  2. **托盘隐藏期间渲染/GPU 进程崩溃 → 唤起白屏**：崩溃处理改为记录标志，隐藏状态下不立即 Reload（无效），恢复窗口时兜底重载页面（含 GPU 进程崩溃，此前未处理）
  3. **长隐藏（>5 分钟）渲染进程被系统回收**（无崩溃事件）：恢复窗口时强制重载页面兜底
- **首次启动要二次点击才能开窗（根因已定位并修复）**：
  1. **根因**：冷启动流程先创建了启动状态窗（IWin32Window），服务就绪、状态窗关闭后 Main 才调用 `Application.SetCompatibleTextRenderingDefault(false)` → 抛出 `InvalidOperationException` → **进程静默崩溃**（Windows 错误报告，无任何提示）→ 主窗口永远不出现。用户看到状态窗消失后"没反应"，再点一次——此时服务已在跑、跳过状态流，才轮到正常的初始化顺序 → 开窗成功。表现为"要点击两次"。修复：`EnableVisualStyles` + `SetCompatibleTextRenderingDefault` 移到 Main 最前面（任何窗口/控件创建之前），已在两台路径实测（冷启动 状态窗→就绪→自动开主窗口，无崩溃）
  2. 就绪判定改为"端口可连 + HTTP 有响应"（此前端口一开就判定成功，但 dsh 前端 HTTP 还要数十秒才就绪，探测过早失败 → 壳退出 → 服务后台继续启动 → 用户二次点击才成功）
  3. **端口已开但 HTTP 前端未就绪时也显示状态窗等待**：此前直接开窗会白屏数十秒（用户以为没反应而多点一次）；现在统一等 HTTP 就绪再开主窗口
  4. 状态窗标题不再与主窗口同为 "DeepSeek Harness"（改为"dsh-launcher 启动中"）：二次点击时单实例逻辑按标题只会找到真正的主窗口并等待其出现，不会把状态窗误当主窗口聚焦（表现为"点了没反应"）；文案注明"完成后会自动打开窗口，请稍候"
  5. 日志错误标志（npm ERR / EACCES / ECONNREFUSED 等）判定加 **15 秒宽限期**：启动过程中的良性告警也会命中这些关键词，此前会立即误判"启动失败"退出；现在宽限期内 HTTP 就绪仍算成功，只有持续失败才报错
  6. **启动日志按端口隔离**（3080 用 `.dsh-web.log`，其他端口用 `.dsh-web.&lt;port&gt;.log`），且被运行中的服务锁定时 vbs 回退到 `%TEMP%`：此前 `.dsh-web.log` 被运行中的 dsh 服务（stdout 重定向）锁定时，vbs 的 `echo > 日志 && dsh web >> 日志` 整条失败（`&&` 串联），**服务根本起不来** → 状态窗永不开窗
  7. 启动轨迹日志：`%LOCALAPPDATA%\dsh-launcher\shell.log` 记录壳的关键决策点（单实例、端口探测、服务拉起、就绪判定、窗口显示），启动异常时可直接查看定位（本轮排障即靠它逐条定位）
- **开机自启默认不勾选未生效**：MSI 条件中非空字符串 `"0"` 被当作 true，`NOT AUTO_START_OPTION` 对默认值不生效（默认仍安装了自启）；改为显式数值比较 `AUTO_START_OPTION <> 1`（实测默认不装、勾选才装）
- **孤儿自启清理**：per-machine 提权卸载跳过 per-user 组件时会残留 HKCU Run 自启项，壳启动时检测其指向的 `start-dsh.vbs` 不存在则自动删除

## [0.1.8] - 2026-08-14

### 修复

- **显示缩放下字体/图标模糊（[issue #2](https://github.com/Ruler4396/dsh-launcher/issues/2)）**：壳未声明 DPI 感知，Windows 在 125%/150% 缩放下对 WebView2 内容做位图拉伸导致模糊（浏览器因为 Per-Monitor DPI aware 而清晰）。修复：Main 第一行调用 `SetProcessDpiAwarenessContext(PerMonitorV2)`（WinForms 的 SetHighDpiMode 在部分环境下因先前的弹窗而失效，改用 user32 直接调用），运行时验证进程 DPI awareness = 2（per-monitor）；主窗口按初始 DPI 放大，保持逻辑大小不缩水

## [0.1.7] - 2026-08-14

### 新增

- **启动依赖预检**：壳在需要自动拉起 dsh 服务前快速检测 Node.js，缺失时立即弹窗提示安装（不再静默等待超时才报"服务不可用"）；WebView2 初始化失败也有明确提示（此前会静默无窗口）
- **服务启动状态窗**：自动拉起服务期间显示"正在启动 dsh 服务…首次运行需要下载组件"的进度提示（可取消）；首次 npx 下载不再是静默等待——超时（3 分钟）会区分"下载较慢/网络问题"并指引日志 `%USERPROFILE%\.dsh-web.log`
- **首次下载差错控制强化**：等待期间持续监控启动日志，出现明确错误（npm ERR、EACCES/ENOSPC/ETIMEDOUT、无 npx、模块缺失等）立即结束等待；失败/超时弹窗**直接附带日志尾部**展示真实原因；端口就绪后额外 HTTP 探测确认是 dsh 服务（防端口被其他程序占用）；页面加载失败也有明确提示（不再白屏静默）
- **服务停留模式（托盘 + 生命周期）**：壳读取 `%LOCALAPPDATA%\dsh-launcher\settings.json` 的 `serviceLifetime`（由配套插件或托盘菜单写入）：`0` 常驻（默认，服务一直运行）/ `1` 托盘驻留（关窗最小化到托盘，托盘"退出"才停服务）/ `2` 跟随窗口（关窗即停服务并退出）。只停壳本次会话拉起的服务（外部托管/用户手动启动的不动）；托盘图标双击切换窗口、右键菜单含**服务模式子菜单**（即时切换）与退出

## [0.1.6] - 2026-08-14

### 变更

- MSI 改为**系统级安装（per-machine）**：安装/卸载会弹一次 UAC 管理员确认，默认装到 `%ProgramFiles%\dsh-launcher`（向导仍支持自定义目录，如已有的 E:\ 目录）；注册表、快捷方式改为 HKLM / 公共桌面 / 公共开始菜单，卸载自动清理
- **旧版本自动清理（安全版）**：壳程序启动时检测机器上是否还有其他版本的 dsh-launcher（per-user 的 0.1.0–0.1.5 等），检测到则提示用户一键提权卸载旧版（提权卸载不会触发 Config.Msi 1926），避免多版本共存；当前运行的版本通过安装时写入的 `HKLM\Software\dsh-launcher\CurrentProductCode` 识别，永远不会被误卸。**识别用固定 UpgradeCode 精确匹配**（读取缓存 MSI 的 UpgradeCode，与 `{3B29D055-...}` 一致才算本产品）——其他恰好同名的软件不会被误清理；弹窗让用户最终确认
- **孤儿快捷方式自愈（安全版）**：per-user 旧版被（提权）卸载后，其用户级开始菜单/桌面快捷方式可能残留（MSI 提权卸载跳过 per-user 上下文组件），壳每次启动自动清理**目标确为 DshWeb.exe** 的快捷方式（读取 .lnk 目标验证），用户自行创建的同名快捷方式（指向其他程序）不受影响
- **应用图标（小鲸鱼）**：壳 exe 编译自带图标资源（此前 exe 无图标，快捷方式与"设置 → 应用"都显示系统默认图标）；MSI 安装的快捷方式与卸载条目现在都显示小鲸鱼图标（`ARPPRODUCTICON` + 显式 `DisplayIcon` 注册表值）

### 修复

- **根治装→卸报错 1926/"无法设置文件…Config.Msi…的安全权限，错误: 5"**。根因：Windows Installer 在**卸载**期仍会创建回滚文件（.rbf）到安装盘根目录的 `Config.Msi`，并以用户身份对其设置安全，而该目录 ACL 由 MSI 服务硬编码为仅 SYSTEM/管理员（任何盘根/目录 ACL 都无法绕过，已实测）；非提权用户（含 UAC 过滤的管理员）在自定义 ACL 的磁盘（如本机 E:\）上必然失败。修复：per-machine 提权后，卸载事务以管理员身份匹配 `Config.Msi` 的 Administrators ACL，不再报错；另保留安装期 `DISABLEROLLBACK=1` 作额外保险。默认目录（C:）与非提权路径本无此问题
- 从 0.1.5（per-user）升级：本机实测可自动升级（RemoveExistingProducts）；标准机器上 per-user 旧版注册在 HKCU、per-machine 新版找不到时，新版启动后会自动提示"检测到旧版本"，一键提权卸载旧版（无需手动清理，也不再有 1926 报错）

> **升级提醒 / For users of older versions**
> 0.1.6 修复了旧版本（per-user，0.1.5 及更早）在部分磁盘上"安装后立即卸载报错 1926/错误 5"的问题，并会自动清理机器上残留的旧版本，**建议尽快更新**。
> 旧版本用户如果之前把 dsh-launcher 装到了 E:\ 等自定义目录，卸载旧版时可能看到 1926/"无法设置文件 Config.Msi 的安全权限，错误 5"提示——这是 Windows Installer 对回滚文件的系统级行为，**报错后产品仍会被正常删除**，不影响结果；更新到 0.1.6 后，新版首次启动会检测到旧版本并提示一键提权卸载（不再有 1926 报错）。如果升级后发现"设置 → 应用"里有两个 dsh-launcher，直接用新版弹出的提示清理即可。

## [0.1.5] - 2026-08-14

### 新增

- 壳支持环境变量 `DSH_WEB_URL` 覆盖目标地址/端口（免重建）；设置后视为外部托管服务、不再自动拉起；单实例锁按目标端口隔离

### 变更

- MSI 安装向导重做：去掉老式"功能树"下拉（将安装在本地硬盘上/整个功能…/功能将在需要时安装/整个功能将不可用 + 重置/磁盘使用量按钮），改为简单向导 + **三个勾选框**（开机自启 / 桌面快捷方式 / 开始菜单快捷方式），卸载快捷方式始终安装
- "选择安装目录"页重新设计为 **Segoe UI 现代风格**：简洁布局 + 直接输入/粘贴路径（默认 `%LOCALAPPDATA%\dsh-launcher`）。注：系统原生文件夹浏览按钮因 Windows Installer 自定义动作在本环境的稳定性问题暂不提供，路径输入完全可靠
- MSI 向导支持**自定义安装目录**
- 卸载安全：卸载仅删除本应用文件，目录仅"空"时移除；与 DeepSeek Harness 等共用目录时其他内容不受影响（已实测验证）
- `uninstall-autostart.cmd` 额外清理旧版 `dsh-autostart.vbs` 自启项

### 修复

- 自动播放被 WebView2 静默拦截（当前 SDK 不触发 Autoplay 权限事件）→ 主窗口与插件弹窗共享同一 WebView2 环境并注入 `--autoplay-policy=no-user-gesture-required`，声音类插件可用
- 打包脚本末尾清理对缺失目录容错

### 测试

- 隔离沙盒端到端实测（全新 `DSH_HOME` + 最新 dsh 0.1.0-rc.6 + dsh-notification / dsh-web-ui-notify 双通知插件共存）：通知权限、剪贴板、自动播放（静音与非静音）、同源弹窗子窗口、下载落盘与同名避让、单实例、双插件共存全部通过；确认 WebView2 会屏蔽 `--remote-debugging-port`（外部 CDP 不可用，测试改用自建测试页 + fetch 回报）
- MSI 向导 UI 自动化验证：三个勾选框取消勾选后安装（自启/桌面/菜单快捷方式均不装，卸载快捷方式保留）、默认全勾选安装、自定义安装目录、与第三方文件共用目录时卸载不误删、卸载零残留均通过

## [0.1.3] - 2026-08-13

### 新增

- MSI 安装向导（WixUI，中文界面）：安装时可勾选是否开机自启；开始菜单新增"卸载 dsh-launcher"快捷方式
- Release 说明自动附带"安装与卸载"段落（MSI vs ZIP 区别移至 Releases 页说明）

### 变更

- README 精简为新手向短文档，详细内容移至 `docs/DETAILS.md`
- 打包脚本健壮性：发布产物完整性校验、自动安装 WiX UI 扩展

## [0.1.2] - 2026-08-13

### 修复

- `start-dsh.vbs` / `start-dsh.cmd`：`dsh` 不在 PATH 时自动回退 `npx -y @deepseek-ai/dsh web` 拉起服务。此前若只通过 `npx` 使用 dsh 而未全局安装，静默自启会失败，表现为“必须先手动跑 `npx @deepseek-ai/dsh web`，壳窗口才会弹出来”；`%USERPROFILE%\.dsh-web.log` 首行现在会写明实际使用的启动方式

### 测试

- 新增 `tests/DshShell.Tests` 单元测试（xunit，55 用例）：弹窗分类、权限策略、下载文件名推导与清理
- 新增 `scripts/test.ps1` 集成测试：脚本静态回归断言、uninstall 行为测试、可选冒烟测试（窗口/单实例）
- CI 增加 `dotnet test` 步骤
- 修复 `blob:`/`data:` 下载文件名问题：不再取随机 UUID 尾段，改为时间戳 + MIME 扩展名
- 修复文件名清理：Windows 保留设备名（`CON`/`NUL`/`COM1` 等，含带扩展名形式）与结尾点/空格现在会被正确处理

## [0.1.1] - 2026-08-13

### 新增

- 壳应用自动授权桌面通知与剪贴板权限（WebView2 `PermissionRequested`），支持 dsh-notification 等通知插件；麦克风/摄像头保持默认拒绝（隐私）
- 权限策略扩充：自动放行自动播放 / 多文件下载 / 持久存储，兼容声音类与批量导出类插件
- 同源弹窗（`window.open()`）改为新建轻量壳窗口：保留会话状态，主窗口不再被导航走；外部链接进系统默认浏览器；`blob:`/`data:` 等保持 WebView2 默认
- `blob:` 无扩展名下载按 MIME 类型自动补扩展名
- WebView2 初始化抽成共用方法（主窗口与弹窗行为一致）
- 下载处理：文件自动保存到系统“下载”文件夹（自动避开同名文件），完成后用默认程序打开
- 渲染进程崩溃/无响应时自动重载页面（10 秒节流，避免死循环）
- 壳应用单实例保护：重复启动自动聚焦已开窗口，不重复创建 WebView2 进程
- 关闭表单自动填充与密码保存，降低后台开销；保留 F12 开发者工具

### 修复

- 卸载脚本 `uninstall-autostart.cmd` 改为删除启动文件夹中的自启项与指向 `DshWeb.exe` 的桌面快捷方式（此前误删不存在的计划任务）
- `dsh-web.cmd` 改为从脚本同目录启动 `DshWeb.exe`，并处理 `start-dsh.vbs` 缺失的情况
- 发布包现在包含全部运行时脚本（`start-dsh.vbs` / `start-dsh.cmd` / `dsh-web.cmd` / `uninstall-autostart.cmd`），部署目录自包含

### 文档

- README：补充 .NET Desktop Runtime 10 运行依赖与安装方式；更新目录结构与构建说明；精简版本兼容性等表述
- README 改为完整中英双语（中文 + English 各一份完整版）

## [0.1.0] - 2026-08-13

### 新增

- WebView2 轻量壳应用（单文件约 1MB）：独立窗口打开 dsh Web UI，替代完整浏览器
- 静默开机自启：`start-dsh.vbs` 无窗口启动服务，无需管理员权限
- 一键入口 `dsh-web.cmd`：检测端口 → 自动拉起服务 → 打开壳窗口
- 壳应用自动拉起：服务未运行时自动启动并等待就绪（最长 90s）
- 日志落盘：服务输出写入 `%USERPROFILE%\.dsh-web.log`
- WebView2 用户数据隔离：存放于 `%LOCALAPPDATA%\DshWeb`，不污染程序目录
- 卸载脚本 `uninstall-autostart.cmd`
- GitHub Actions CI：自动构建 Windows 发布包，tag 推送自动发布 Release
- 打包脚本 `scripts/build-release.ps1`

### 文档

- README（中文为主 + 英文简介）：快速开始、内存对比、目录结构、FAQ
- 贡献指南、安全说明、行为准则、Issue/PR 模板
