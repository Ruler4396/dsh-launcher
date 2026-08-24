# dsh-launcher 详细文档 / Details

> README 之外的完整细节：技术实现、安全、发版策略、构建、测试、目录结构、FAQ。

## 内存对比 / Memory comparison

| 方案 | 平时占用 | 打开界面后 |
| --- | --- | --- |
| 浏览器访问（Edge/Chrome 常驻） | 500MB+ | 更高 |
| 本工具 | 仅 dsh 服务（Node 进程，约 100–200MB） | 壳窗口 50–150MB，关闭即释放 |

> dsh 服务本身是 Node.js 进程，无论用什么前端打开都必须常驻；本工具省去的是"完整浏览器"这部分开销。
> The dsh service itself is a Node.js process that must stay resident regardless of the frontend; this tool only removes the "full browser" overhead.

## 技术实现 / How it works

```text
┌─────────────┐   HTTP    ┌──────────────────────────────┐
│  DshWeb.exe │ ────────► │  dsh 服务（node 进程）        │
│  (壳, 单文件) │  127.0.0.1│  dsh web --host 127.0.0.1    │
│  WinForms +  │ ◄──────── │      --port 3080             │
│  WebView2    │  Web UI   │  数据/插件/会话都在 DSH_HOME  │
└──────┬──────┘            └──────────────────────────────┘
       │ 启动/停止/就绪探测（端口+HTTP）/PID 记录
       │ start-dsh.vbs（wscript 静默拉起，输出 append 统一日志）
       ▼
   统一日志 ~/.dsh/dsh-launcher/dsh.log（JSON Lines + 服务输出共存）
```

| 模块 | 方案 |
| --- | --- |
| 壳应用 | WinForms + `Microsoft.Web.WebView2`，`PublishSingleFile` 单文件发布 |
| 静默启动 | VBS 调用 `wscript` 后台运行 `dsh web --host 127.0.0.1 --port 3080`，输出按 `DSH_LOG` 追加到统一日志 `DSH_HOME\dsh-launcher\dsh.log`（append 模式，**不截断、不自行轮转**——轮转所有权归壳）；日志被运行中服务锁定或缺失时回退 `%TEMP%\dsh.log` 优先保证冷启动；`dsh` 不在 PATH 时自动回退 `npx -y @deepseek-ai/dsh web` |
| 端口探测 | `ConnectAsync`（`ShellLogic.PortOpenAsync`，3s 超时）——v0.4.0 起异步化，不再阻塞调用线程；壳启动时探测、未就绪则轮询等待（最长 180s）；目标默认 `3080`，可用环境变量 `DSH_WEB_URL` 覆盖（免重建），设置后视为外部托管服务、不再自动拉起 |
| 启动体验（v0.4.0 极速启动） | `SplashForm`（Windows/SplashForm.cs）双缓冲渲染，后台流水线经 `IProgress<T>` 回填进度，双击后 <500ms 出现启动窗；Node 缺失/服务超时等确认交互走窗体内联面板（非 MessageBox 嵌套模态循环）；编排唯一由组合根 `LauncherApp` 驱动（ADR-010） |
| UI 自动化（TestHook） | `DSH_TEST_MODE=1` + `--ui-probe` 时激活 NamedPipe（`Win32/UiTestHook.cs`，ADR-009），命令 `ToggleMaximize`/`GetWindowRect`/`GetWorkArea`/`Shutdown`；与 `--ui-selftest`（进程内自测）互补，供 E2E 精确验证最大化 0px 间隙 |
| 开机自启 | MSI 勾选后写 `HKCU\...\Run` 直接指向 `DshWeb.exe`（登录 → 壳窗口出现 → 壳自行拉起服务）；安装器同时落 HKLM 意图标志（per-machine 提权安装直接写 HKCU 不可靠，壳首启补写兜底）；便携版：启动文件夹放置 `start-dsh.vbs` 由 `wscript` 执行，或直接放 `DshWeb.exe` 快捷方式 |
| 权限 | `PermissionRequested` 自动放行：通知、剪贴板、多文件下载、持久存储（插件兼容），麦克风/摄像头保持默认拒绝；自动播放经共享 WebView2 环境注入的 `--autoplay-policy=no-user-gesture-required` 放行（当前 SDK 不会为 Autoplay 触发权限事件，只能走浏览器参数） |
| 下载 | 保存到系统"下载"文件夹（同名自动改名），blob: 按 MIME 补扩展名，完成后默认程序打开 |
| 弹窗 | 外部 http(s) → 系统默认浏览器；同源弹窗新建轻量窗口（保留会话）；blob:/data: 保持默认 |
| 崩溃自愈 | 渲染进程崩溃/无响应自动重载（10 秒节流） |
| 单实例 | 按目标端口隔离的互斥锁：重复启动自动聚焦已开窗口，不重复创建 WebView2 进程 |
| 安装包 | WiX v5 per-machine MSI（安装/卸载需 UAC 提权）：默认 `%ProgramFiles%\dsh-launcher` 可自定义，无服务、无计划任务，可卸载 |

### 架构决策索引（Architecture decisions）

源码注释只保留"不变式/半步警示"；**为什么这样设计、历史演进、issue 编号**统一沉淀到这里，避免源码被历史注释淹没。Bug 历史见 `CHANGELOG.md` / GitHub issues。

| ID | 决策 | 一句话理由 |
| --- | --- | --- |
| ADR-001 | **去掉 `WS_CAPTION`，仅留 `WS_THICKFRAME|WS_MINIMIZEBOX|WS_MAXIMIZEBOX|WS_SYSMENU`** | 无 WS_CAPTION 则 DWM 最大化时不再为原生标题栏预留非客户区且不把窗口外扩，`WM_GETMINMAXINFO` 直接给工作区即 0px 精确铺满（消除 4px 间隙）；Aero Snap/Win+方向/Alt+Space/任务栏收起所需的位仍在 |
| ADR-002 | **`WM_NCCALCSIZE` 返回 0，且严禁把 `rgrc0` 钳到工作区** | 客户区=窗口矩形；钳制会让客户区小于窗口，残留区被 DWM 当原生标题栏绘制（"多出一栏"）；最大化边界对齐全交给 `WM_GETMINMAXINFO` |
| ADR-003 | **拦截 `WM_NCACTIVATE`(返回1)/`WM_NCPAINT`(返回0)** | 不拦则 DefWindowProc 用经典 NC 渲染器画 Win98 式标题栏（样式带 SYSMENU/MINIMIZEBOX/MAXIMIZEBOX）；本窗口 NC 全部自绘 |
| ADR-004 | **F11 用系统级低级键盘钩子（`WH_KEYBOARD_LL`）** | 物理 F11 进 WebView2 浏览器进程，`KeyDown/ProcessCmdKey/消息过滤器`都拦不到；钩子在 OS 层捕获，仅前台时切换最大化/还原 |
| ADR-005 | **便携 Node 校验和固定优先从官方 `nodejs.org` 拉取** | 校验和若与 zip 同镜像源，镜像被投毒则"防篡改"失效；官方优先、镜像回退（供应链） |
| ADR-006 | **日志轮转绕开"活服务占用"** | 崩溃残留的孤儿服务若仍用 `cmd >>` 持有日志，`File.Move` 会把日志劈裂成两段；无活服务才轮转 |
| ADR-007 | **JSON 状态文件用原子写（临时文件+`File.Move`）** | 防关窗/退出瞬间崩溃留下半截 JSON；窗口位置、暂存更新、镜像记忆共用 `ShellLogic.AtomicWrite` |
| ADR-008 | **启动/退出用纯内存状态机 `LauncherLifecycle`** | 替代 Main 面条代码的隐式状态，纯表可 Headless 单测；独立类不经 UI，重构期间先建回归护栏再接线；`WebViewCrashed` 触发器用 Running 自转移表达"崩溃被拦截并重载，不终结应用" |
| ADR-009 | **UI TestHook 用 NamedPipe（`DSH_TEST_MODE=1` 激活）** | WinForms UI 几何状态难自动化；pipe 提供进程内可控入口发 `ToggleMaximize`/`GetWindowRect`/`GetWorkArea`/`Shutdown`，按进程 PID 命名隔离并行 E2E，生产路径零接触 |
| ADR-010 | **启动编排唯一由组合根 `LauncherApp` 驱动** | 消除"新旧双实现并存（语义漂移）"：Manager 装配 + `LauncherLifecycle` 状态→副作用接线集中在组合根，`Program.Main` 只做薄适配；目标地址/端口经 `ShellLogic.ResolveTarget` 统一解析（`DSH_WEB_URL` 外部托管 / `DSH_WEB_PORT` 端口覆盖 / 缺省 3080） |
| ADR-011 | **`WindowManager` 对 `Program` 的静态引用一律改组合根委托注入** | 切断 Program↔WindowManager 隐式循环依赖；`PopupFactory`/`ApplyShadowAction`/`ShowWindowAction`/`ResolveDarkModeProvider`/`TraceAction` 由组合根装配，进程级测试不被 Program 静态状态污染 |
| ADR-012 | **Splash 消息泵模型：双缓冲 + `IProgress<T>` 回填 + 内联确认面板** | UI 线程只跑消息泵，全部耗时 IO 在后台流水线；进度经绑定 UI `SynchronizationContext` 的 `Progress<T>` 回填，确认用窗体内联面板 + `TaskCompletionSource`（禁 MessageBox 嵌套模态循环）；控件构造时预渲染默认文本，消除白屏/闪烁 |
| ADR-013 | **服务探测必须异步（`ConnectAsync`/后台线程）** | 同步 `TcpClient.Connect` 本机可达 2s，在 UI 线程会卡死消息泵（白屏）；`ShellLogic.PortOpenAsync` + `ServiceManager` 探测异步化，首个 `await` 前不做任何阻塞 IO |

## 错误码表 / Error codes

弹窗、统一日志的 `code` 字段、`--diagnose` 汇总共用同一套码（目录见 `src/DshShell/ErrorCodes.cs`）。

| 码 | 含义 |
| --- | --- |
| E1002 | 用户拒绝自动安装便携 Node.js |
| E1003 | 便携 Node.js 下载失败（网络或镜像问题） |
| E1004 | 便携 Node.js 校验和不匹配，已拒绝使用（防供应链篡改） |
| E1005 | 便携 Node.js 解压失败 |
| E1006 | 缺少 WebView2 Runtime（Edge WebView2），无法渲染窗口 |
| E1007 | 渲染进程反复崩溃，已停止自动重载（可通过托盘唤窗或重新打开恢复） |
| E2001 | 缺少 start-dsh.vbs，无法自动拉起 dsh 服务 |
| E2002 | dsh 服务启动超时（下载较慢或网络/代理问题） |
| E2003 | dsh 服务启动日志出现错误（npm/权限/依赖问题） |
| E2004 | dsh 服务不可用（端口无 HTTP 响应） |
| E2005 | 检测到上次崩溃遗留的异常服务进程，已清理 |
| E2006 | 启动已取消（服务可能仍在后台下载/启动，下次启动可接管） |
| E2011 | dsh-launcher-lifetime 插件已卸载，已忽略残留的常驻配置并按默认模式运行 |
| E4001 | dsh 新版本下载失败 |
| E4002 | dsh 延迟更新应用失败，将继续使用当前版本 |
| E5001 | 诊断日志导出失败 |
| E9001 | 内部未分类错误 |

## 安全说明 / Security

- **系统级安装（per-machine，提权）**：安装/卸载会弹一次 UAC 管理员确认，默认安装到 `%ProgramFiles%\dsh-launcher`，向导中可自定义安装目录；不注册服务、不创建计划任务。提权同时是卸载零报错的保证：Windows Installer 在卸载期对安装盘根 `Config.Msi` 里的回滚文件（.rbf）以用户身份设置安全，而该目录 ACL 硬编码为仅 SYSTEM/管理员，非提权在 ACL 异常的磁盘（如 E:\）必报 1926（详见 FAQ）
- **卸载只删自己的文件**：MSI 卸载仅移除本应用安装的文件；目录只会在"空"时才被删除，预先存在的文件（如与 DeepSeek Harness 共用目录）绝不会被误删（已实测验证）
- **卸载/清理的用户数据边界（v0.3.0）**：MSI 卸载会自动清理启动器自有数据——`DSH_HOME\dsh-launcher\` 整目录（settings.json 残留、统一日志 dsh.log、window-state/pending-update/service-pid 等）与旧版 `%USERPROFILE%\.dsh-web*.log`；便携版需显式运行 `uninstall-autostart.cmd -CleanData` 才清理（不带参数只清自启/快捷方式，绝不 surprise-delete）。**硬边界：两者都绝不触碰 `DSH_HOME` 下其余内容——`profiles/`、`settings.yaml`、`.credentials.yaml`、sessions、storages、插件等一切 dsh 生态数据**。
- **自启仅当前用户（拉壳方案）**：安装器写机器级意图标志（`HKLM\Software\dsh-launcher\AutoStartWanted`，随卸载自动清除）并写当前用户 `HKCU\...\Run` 指向 `DshWeb.exe`；壳首启若发现标志存在但 Run 值缺失/格式旧（如旧版 wscript+vbs），自动以当前用户身份补写/迁移。卸载或 `uninstall-autostart.cmd` 时删除 Run 值与意图标志（防止壳自愈复活）
- **下载校验**：每次 Release 附带 `SHA256SUMS.txt`
- **代码签名**：安装包当前未签名，SmartScreen 可能提示"未知发布者"（正常）；正式分发建议购买代码签名证书
- **数据本地化**：WebView2 数据在 `%LOCALAPPDATA%\DshWeb`；统一日志在 `~/.dsh\dsh-launcher\dsh.log`（壳的 JSON Lines 与 dsh 服务原始输出同文件共存，`DSH_LOG_LEVEL` 控级别），无遥测。**v0.3.0 起不再产生旧版的多文件日志**（`shell.log`、`%USERPROFILE%\.dsh-web*.log`）

## 发版策略 / Release policy

- **严重问题/安全修补** → 立即打补丁版本 tag（`vX.Y.Z+1`）发版，CHANGELOG 同步更新
- **新功能** → 升次版本号发版
- 每次 tag 推送，CI 自动：跑单测 + 脚本回归（`test.ps1`）→ 构建 zip + MSI + SHA256 校验和（附 Release body）→ 从 CHANGELOG 生成 Release 说明并发布

## 版本兼容性 / Version compatibility

- 只调用 `dsh web` 的 CLI（`--host` / `--port`）、默认端口 `3080` 和 Web UI 的 HTTP 访问，不依赖 dsh 内部实现，dsh 升级一般无需重新编译壳；壳的目标地址可用环境变量 `DSH_WEB_URL` 覆盖（默认 `http://127.0.0.1:3080`）
- **运行时身份假设（P1-8 人工调研结论）**：dsh 服务目前以 `node` 进程名监听目标端口（`IsLikelyDshService` 身份校验依据）；若上游更换运行时（非 node），需同步调整该判定，否则"跟随窗口"关闭时无法停服务（宁可拒绝杀，也不误杀无关进程）。Node 可用门槛：主版本 ≥18（`IsUsableNodeVersion` 单一判定点；`DSH_NODE_VERSION` 可覆盖便携版版本）
- `npm update -g @deepseek-ai/dsh` 后重启服务即可
- dsh 处于开发者预览阶段，若官方变更启动参数或默认端口：壳侧设置 `DSH_WEB_URL` 即可免重建；自启脚本需同步修改 `start-dsh.vbs`、`dsh-web.cmd` 两处
- 本工具不锁定 dsh 版本，始终跟随本地最新版

## 从源码构建 / Building from source

**方式一：完整发布（zip + MSI 安装包）**，需要 [WiX v5](https://wixtoolset.org/)：

```powershell
git clone https://github.com/Ruler4396/dsh-launcher.git
cd dsh-launcher
dotnet tool install --global wix --version 5.0.2   # 一次性
./scripts/build-release.ps1 -Version 0.3.1          # zip + MSI + SHA256（zip 带版本号命名）
```

**方式二：只需源码编译（无需 WiX）**——只编译壳 + 复制部署脚本：

```powershell
git clone https://github.com/Ruler4396/dsh-launcher.git
cd dsh-launcher
dotnet publish src/DshShell -c Release -r win-x64
# 产物在 src/DshShell/bin/Release/net10.0-windows/win-x64/publish/
copy scripts\start-dsh.vbs, scripts\start-dsh.cmd, scripts\dsh-web.cmd, scripts\uninstall-autostart.cmd `
  src\DshShell\bin\Release\net10.0-windows\win-x64\publish\
# 运行：
src\DshShell\bin\Release\net10.0-windows\win-x64\publish\DshWeb.exe
```

构建产物：`DshWeb.exe`（框架依赖单文件，约 1MB，需 .NET Desktop Runtime 10）、`dsh-launcher-<版本>.msi`、`SHA256SUMS.txt`。

## 测试 / Testing

```powershell
dotnet test tests/DshShell.Tests    # 单元测试（ShellLogic：弹窗分类/权限策略/文件名）
./scripts/test.ps1                  # 集成检查（脚本回归断言 + uninstall 行为）
./scripts/test.ps1 -Smoke           # 追加冒烟测试（需 dsh 服务在运行且已构建 dist）
```

CI 每次 push/PR 也会自动跑 `dotnet test`。

## 目录结构 / Directory layout

```
dsh-launcher/
├── README.md                   # 用户文档（中文主入口；英文版 docs/README.en.md）
├── CHANGELOG.md
├── LICENSE
├── assets/                    # README 截图（浅色/深色）
├── .github/                   # 社区治理与 CI
│   ├── CODE_OF_CONDUCT.md / CONTRIBUTING.md / SECURITY.md
│   ├── ISSUE_TEMPLATE/ + PULL_REQUEST_TEMPLATE.md
│   └── workflows/build.yml
├── docs/DETAILS.md            # 本文件：实现细节
├── installer/                 # MSI 安装器
│   ├── product.wxs            # WiX v5 源：per-machine MSI（自启/快捷方式/目录选择向导）
│   ├── FolderPicker/          # 目录选择器 exe（Type-38，新版文件夹对话框）
│   ├── FolderPickerCa/        # DTF 托管 CA（net20，读回所选目录 + 自启注册表动作）
│   └── PrereqCheck/           # 前置检查 exe（Type-38，检测 .NET 10 / Node.js）
├── scripts/                   # 部署脚本（发布包内与 DshWeb.exe 同目录）
│   ├── start-dsh.vbs          # 静默启动服务（壳拉起服务用）
│   ├── start-dsh.cmd / dsh-web.cmd  # 调试启动 / 一键入口
│   ├── check-prereq.cmd       # 便携版环境自检（.NET/WebView2/Node，随发布包分发）
│   ├── uninstall-autostart.cmd      # 清理自启与快捷方式
│   ├── test.ps1 / negative-test.ps1 / e2e-test.ps1 # 测试（仅开发用）
│   └── build-release.ps1      # 打包（仅开发用）
├── src/
│   └── DshShell/              # 壳应用源码（C# WinForms + WebView2）
│       ├── Lifecycle/         # 纯内存状态机 LauncherLifecycle（ADR-008）
│       ├── Managers/          # 五个职责 Manager（Runtime/Service/WebView/Window/Tray）+ F11 钩子
│       ├── Windows/           # 窗体类（DshShellForm / SplashForm / TrayMenuForm）
│       ├── Win32/             # Win32 封装（NativeMethods / WindowGeometry / DisplayMetricsProvider / UiTestHook）
│       ├── Chrome/            # 自绘标题栏 CustomTitleBar / WindowChromeController
│       ├── LauncherApp.cs     # 组合根：装配 Manager + 状态→副作用接线（ADR-010）
│       ├── ShellLogic.cs      # 纯策略逻辑（可单测）
│       ├── Program.cs         # 入口 + UI 事件接线（已大幅瘦身）
│       └── …
└── tests/
    ├── DshShell.Tests/        # 单元测试（含 Lifecycle/、Managers/ 目录）
    └── DshShell.E2E/          # E2E：启动耗时基准 / UI 响应性 / 跨屏最大化 / TestHook
```

## 常见问题 / FAQ

**Q：端口 3080 被占用怎么办？**
设置环境变量 `DSH_WEB_URL=http://127.0.0.1:<新端口>` 再启动壳即可（免重建）；若还需要壳自动拉起服务，则同步修改 `start-dsh.vbs`、`dsh-web.cmd` 中的端口。

**Q：为什么不用 Electron / Tauri？**
Electron 自带完整 Chromium（与浏览器同级的内存开销）；Tauri 底层同样是 WebView2 但需要 Rust 工具链。本工具直接用 WebView2 封装，产物更小、构建更简单。

**Q：dsh-notification 等插件的桌面通知从来没弹过？**
最常见原因是**壳没给 WebView2 授权通知权限**（插件客户端 `api.permission !== 'granted'` 时直接不弹）。0.1.2 起的构建已在 `PermissionRequested` 中自动授权，请确认用的是新版本。验证步骤：
1. 设置 → 通知 → 确认"启用通知"打开、权限状态显示"已授权"，点"发送测试通知"应立刻弹出
2. 插件默认"仅在后台时通知"：**窗口最小化/隐藏时才弹**（插件的后台判定用的是 `document.hidden`，只有"最小化/隐藏"才会变 true；**最大化但被其他窗口遮挡、不在前台时页面仍视为可见，默认不会弹**）。想最大化下也弹，把"仅在后台时通知"关掉即可（或在其他窗口操作前先把 dsh 窗口最小化）
3. 页面必须保持打开（可后台）；连接中断期间完成的回合不会补发
4. 仍不生效：F12 → Console 过滤 `dsh-notification`，看 `show=false` 时括号里的原因（`permission=` / `backgroundOnly=` / `hidden=` / `focus=`）

**Q：日志里出现 [E2001]"未找到 start-dsh.vbs"，但文件明明在？**
single-file 发布（`PublishSingleFile=true`）下若宿主被 `wscript` 间接调用、或运行时把原生资源解压到临时目录，`AppContext.BaseDirectory` 可能指向临时解压目录而非 exe 所在目录，导致相对定位 vbs 失败。排查：用统一日志确认壳的实际工作目录与 vbs 探测路径；安装版/便携版请从正式发布包启动，不要从临时解压目录直接双击中间产物。

**Q：MSI 和 ZIP 有什么区别？**
见 [Releases](https://github.com/Ruler4396/dsh-launcher/releases) 页面的"安装与卸载"说明。

**Q：能自定义安装目录吗？卸载会不会误删同目录的其他文件？**
MSI 向导中有"选择安装目录"一步（Segoe UI 现代风格，可直接输入/粘贴路径，默认 `%ProgramFiles%\dsh-launcher`）。卸载只会删除本应用的 7 个文件；目录仅当"空"时才会被移除——如果你把 dsh-launcher 装进已有的目录（如 DeepSeek Harness 目录），卸载后该目录和里面的其他文件都会原样保留（已实测验证）。

**Q：安装/卸载报"无法设置文件…Config.Msi…的安全权限，错误: 5"或"错误 1926"？或一直提示"另一个安装正在进行中"(1618)？**
这是 **Windows Installer 的系统级行为**：安装/卸载事务会在目标盘的根目录创建 `Config.Msi`，用于保存回滚脚本与回滚文件（.rbf）。该目录的 ACL 由 MSI 服务（SYSTEM）创建时硬编码为**仅 SYSTEM 和管理员**（不继承盘根 ACL，任何盘根/目录 ACL 都无法绕过）。非提权用户（包括 UAC 过滤后的管理员）在**卸载**时需要对 .rbf 执行"设置安全"，在 `Config.Msi` ACL 异常或用户权限受限的磁盘上（如本机自定义 ACL 的 E:\）必然报 1926/错误 5。

> **本安装包已根治**：0.1.6 起改为**系统级安装（per-machine）**，安装/卸载都以管理员身份运行，事务能匹配 `Config.Msi` 上的 Administrators ACL，卸载不再报 1926（默认目录 C:\ 与非提权路径本无此问题；E:\ 自定义目录装→卸已实测零错误）。另保留安装期 `DISABLEROLLBACK=1` 作额外保险（本包仅 7 个文件，放弃安装期回滚代价可接受）。其他 MSI 包在同类磁盘上仍可能报错，可用下面的手动步骤修复：
1. 关闭所有安装程序，确认任务管理器里没有 msiexec.exe 在运行
2. 管理员运行 CMD，对安装盘根目录（按报错路径，如 `E:\`）执行：
   ```cmd
   takeown /f E:\Config.Msi /r /d y
   icacls E:\Config.Msi /reset /t /c
   rmdir /s /q E:\Config.Msi
   ```
3. 若还提示 1618，重启 Windows Installer 服务：`sc stop msiserver` 后 `sc start msiserver`
4. 重新安装/卸载即可；正常事务结束后 `Config.Msi` 会自动清理

> 预防：**不要在安装进行中强杀 msiexec 进程**，这是 `Config.Msi` 损坏的最常见原因。

**Q：安装后"开始"屏幕/固定区里没有图标？**
这是 Windows 平台限制：**MSI 快捷方式只会进入"所有应用"列表，无法自动固定到"开始"屏幕的磁贴/固定区**（自动固定只有 UWP 应用或用户手动操作才能做到，且没有官方 API）。安装后请到：开始菜单 →"所有应用"→ 找到 **dsh-launcher** → 右键 →"固定到'开始'屏幕"（或"固定到任务栏"）。安装包已自动创建开始菜单里的"DeepSeek Harness"与"卸载 dsh-launcher"快捷方式。
