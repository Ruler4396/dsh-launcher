# dsh-launcher

[![build](https://github.com/Ruler4396/dsh-launcher/actions/workflows/build.yml/badge.svg)](https://github.com/Ruler4396/dsh-launcher/actions/workflows/build.yml)
[![license](https://img.shields.io/github/license/Ruler4396/dsh-launcher)](LICENSE)
[![release](https://img.shields.io/github/v/release/Ruler4396/dsh-launcher)](https://github.com/Ruler4396/dsh-launcher/releases)
[![stars](https://img.shields.io/github/stars/Ruler4396/dsh-launcher)](https://github.com/Ruler4396/dsh-launcher)

[**中文**](#中文) · [**English**](#english)

> DeepSeek Harness 的 Windows 低占用桌面启动器：开机自启 + 轻量独立窗口，摆脱手动敲命令、浏览器高内存占用。

---

## 中文

### 简介

[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（`dsh`）官方目前以 `npx @deepseek-ai/dsh web` 的方式运行，需要每次手动输入命令，并通过浏览器访问 `http://127.0.0.1:3080`。本工具在 Windows 下提供一套替代方案：

- **开机自启**：登录后自动静默启动 dsh 服务，不弹任何窗口
- **一键启动**：桌面图标双击即开，服务未运行时自动拉起
- **低内存**：使用 WebView2 嵌入式内核的独立窗口替代完整浏览器（作者环境实测约 50–150MB，关窗即释放；Edge/Chrome 常驻通常 500MB+）

### 特性

- 🚀 **静默自启**：VBS 无窗口启动器 + Windows 启动文件夹，无需管理员权限即可注册开机自启
- 🪟 **轻量壳窗口**：C# WinForms + WebView2 单文件发布（约 1MB），无地址栏、无扩展、无后台常驻
- 🔌 **自动拉起**：壳应用启动时探测 3080 端口，服务未运行则自动拉起并等待就绪
- 📋 **日志落盘**：服务输出写入 `%USERPROFILE%\.dsh-web.log`，便于排查
- 🧹 **可完全卸载**：`uninstall-autostart.cmd` 删除自启项与桌面快捷方式

### 内存对比

| 方案 | 平时占用 | 打开界面后 |
| --- | --- | --- |
| 浏览器访问（Edge/Chrome 常驻） | 500MB+ | 更高 |
| 本工具 | 仅 dsh 服务（Node 进程，约 100–200MB） | 壳窗口 50–150MB，关闭即释放 |

> 说明：dsh 服务本身是 Node.js 进程，无论用什么前端打开都必须常驻；本工具省去的是“完整浏览器”这部分开销。

### 环境要求

| 依赖 | 版本 | 用途 |
| --- | --- | --- |
| Windows 10/11 | — | 需要内置 WebView2 Runtime（Win11 及新版 Win10 已自带） |
| Node.js | 18+ | 运行 dsh |
| .NET Desktop Runtime | 10.0+ | 运行壳应用 `DshWeb.exe` 所需；未安装时首次启动会提示，见 FAQ |
| .NET SDK | 10.0+ | 仅从源码构建时需要 |

### 快速开始

#### 方式一：直接使用编译产物（推荐）

从 [Releases](https://github.com/Ruler4396/dsh-launcher/releases) 下载 `dsh-launcher-windows.zip`，解压后（内含 `DshWeb.exe`、`WebView2Loader.dll`、`runtimes\` 及全部部署脚本）：

1. 安装 dsh：

   ```powershell
   npm install -g @deepseek-ai/dsh
   ```

2. 运行 `DshWeb.exe`，首次启动会自动拉起 dsh 服务并打开界面。若提示缺少 .NET Desktop Runtime，先执行 `winget install Microsoft.DotNet.DesktopRuntime.10`（见 FAQ）

3. （可选）开机自启：把 `start-dsh.vbs` 复制到启动文件夹（`Win+R` 输入 `shell:startup` 回车）

4. （可选）桌面快捷方式：右键 `DshWeb.exe` → 发送到 → 桌面快捷方式

5. （卸载）运行 `uninstall-autostart.cmd` 删除自启项与桌面快捷方式

#### 方式二：从源码构建

```powershell
git clone https://github.com/Ruler4396/dsh-launcher.git
cd dsh-launcher
./scripts/build-release.ps1    # 一键打包，产出 dist\dsh-launcher-windows.zip（含全部脚本）
```

或手动 publish（记得把 scripts 下的部署脚本一并放入产物目录）：

```powershell
dotnet publish src/DshShell -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o dist
copy scripts\start-dsh.vbs, scripts\dsh-web.cmd, scripts\uninstall-autostart.cmd dist\
```

构建产物为 `dist\DshWeb.exe`（框架依赖单文件，约 1MB，运行需要 .NET Desktop Runtime 10），其余步骤同方式一。

### 目录结构

```
dsh-launcher/
├── README.md
├── LICENSE
├── scripts/                 # 部署脚本（发布包内含全部脚本，与 DshWeb.exe 同目录）
│   ├── start-dsh.vbs        # 无窗口静默启动服务（自启/壳拉起共用）
│   ├── start-dsh.cmd        # 前台调试启动（带日志窗口）
│   ├── dsh-web.cmd          # 一键入口：检查端口 → 拉起服务 → 打开壳
│   ├── uninstall-autostart.cmd  # 删除自启项与桌面快捷方式
│   └── build-release.ps1    # 打包脚本（仅开发用，不随发布包分发）
└── src/
    └── DshShell/            # 轻量壳应用源码（C# WinForms + WebView2）
        ├── DshShell.csproj
        └── Program.cs
```

### 技术实现

| 模块 | 方案 |
| --- | --- |
| 壳应用 | WinForms + `Microsoft.Web.WebView2`，`PublishSingleFile` 单文件发布 |
| 静默启动 | VBS 调用 `wscript` 后台运行 `dsh web --host 127.0.0.1 --port 3080`，输出重定向到日志 |
| 端口探测 | `TcpClient.Connect("127.0.0.1", 3080)`，壳启动时探测、未就绪则轮询等待（最长 90s） |
| 开机自启 | 启动文件夹放置 `start-dsh.vbs`，登录时由 `wscript` 无窗口执行，无需管理员权限 |
| 通知权限 | WebView2 `PermissionRequested` 自动授权桌面通知与剪贴板（配合 dsh-notification 等插件），麦克风/摄像头保持默认拒绝 |
| 下载 | 文件自动保存到系统“下载”文件夹（避开同名），完成后用默认程序打开 |
| 外链 | `window.open()` 的外部链接交给系统默认浏览器，内部弹窗留在壳内 |
| 崩溃自愈 | 渲染进程崩溃/无响应时自动重载页面（10 秒节流，避免死循环） |
| 单实例 | 重复启动自动聚焦已开窗口，不重复创建 WebView2 进程 |
| 端口 | 默认 `3080`，与 dsh 默认一致；如需修改，同步改 `start-dsh.vbs`、`dsh-web.cmd`、`Program.cs` 三处 |

### 版本兼容性

- **依赖面小**：本工具只调用 `dsh web` 的 CLI 接口（`--host` / `--port`）、默认端口 `3080` 和 Web UI 的 HTTP 访问，不依赖 dsh 内部实现，因此 dsh 升级一般无需重新编译壳应用
- **升级方式**：`npm update -g @deepseek-ai/dsh` 后重启服务即可；壳应用无需重新编译
- **注意事项**：dsh 目前处于开发者预览阶段（官方声明未来可能出现破坏兼容性的变更）。若官方变更启动参数或默认端口，只需同步修改 `start-dsh.vbs`、`dsh-web.cmd`、`Program.cs` 三处即可
- 本工具不会修改或锁定 dsh 版本，始终跟随你本地安装的最新版

### 常见问题

**Q：启动后页面打不开？**
查看 `%USERPROFILE%\.dsh-web.log`，确认 dsh 是否安装（`dsh --version`）以及端口是否被占用。

**Q：端口 3080 被占用怎么办？**
修改 `start-dsh.vbs`、`dsh-web.cmd`、`Program.cs` 中的端口号后重新构建/部署。

**Q：提示需要安装 .NET Desktop Runtime？**
壳应用是框架依赖发布，运行需要 .NET Desktop Runtime 10。安装：`winget install Microsoft.DotNet.DesktopRuntime.10`，或从 [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0) 下载安装后重试。

**Q：如何彻底卸载？**
运行部署目录下的 `uninstall-autostart.cmd`（自动删除自启项与桌面快捷方式），最后删除整个部署目录即可。

**Q：为什么不用 Electron / Tauri？**
Electron 自带完整 Chromium（与浏览器同级的内存开销）；Tauri 底层同样是 WebView2 但需要 Rust 工具链。本工具直接用 WebView2 封装，产物更小、构建更简单。

### 免责声明

本仓库是**独立的第三方工具**，与 DeepSeek / DeepSeek AI 官方无关。[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（`dsh`）是官方项目，遵循其自身协议（MIT）。本工具仅在其之上做本地启动与展示层面的封装，不包含官方代码，也不修改官方行为。

窗口图标使用了 DeepSeek 品牌标识（`favicon.png`），该标识的版权归 DeepSeek 所有，仅作个人本地使用，请勿在商业发行物中单独使用。

### 许可证

[MIT](LICENSE) © dsh-launcher contributors

---

## English

> A lightweight Windows launcher for DeepSeek Harness: silent autostart at logon + a minimal WebView2 window instead of a full browser.

### Overview

[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) (`dsh`) is normally run via `npx @deepseek-ai/dsh web`, which requires typing the command every time and opening `http://127.0.0.1:3080` in a browser. This project offers a Windows alternative:

- **Autostart** — the dsh service starts silently after logon, no windows shown
- **One-click launch** — double-click a desktop icon; the service is started automatically if it is not running
- **Low memory** — a standalone WebView2 window replaces the full browser (roughly 50–150MB in the author's environment, freed when the window closes; Edge/Chrome usually idle at 500MB+)

### Features

- 🚀 **Silent autostart** — a windowless VBS launcher in the Startup folder, no admin rights required
- 🪟 **Lightweight shell window** — C# WinForms + WebView2 published as a single ~1MB file; no address bar, no extensions, no resident background process
- 🔌 **Auto-launch** — the shell probes port 3080 on startup and starts the service if it is down, waiting until it is ready
- 📋 **File logging** — service output goes to `%USERPROFILE%\.dsh-web.log` for troubleshooting
- 🧹 **Fully removable** — `uninstall-autostart.cmd` removes the autostart entry and desktop shortcuts

### Memory comparison

| Approach | Idle | With the UI open |
| --- | --- | --- |
| Browser (Edge/Chrome resident) | 500MB+ | higher |
| This project | dsh service only (Node process, ~100–200MB) | shell window 50–150MB, released on close |

> Note: the dsh service itself is a Node.js process that must stay resident regardless of the frontend; this project only removes the "full browser" overhead.

### Requirements

| Dependency | Version | Purpose |
| --- | --- | --- |
| Windows 10/11 | — | WebView2 Runtime required (built into Windows 11 and recent Windows 10) |
| Node.js | 18+ | runs dsh |
| .NET Desktop Runtime | 10.0+ | required to run the `DshWeb.exe` shell app; you are prompted on first launch if missing (see FAQ) |
| .NET SDK | 10.0+ | only needed to build from source |

### Quick start

#### Option 1: prebuilt release (recommended)

Download `dsh-launcher-windows.zip` from the [Releases](https://github.com/Ruler4396/dsh-launcher/releases) page and extract it (it contains `DshWeb.exe`, `WebView2Loader.dll`, `runtimes\` and all deployment scripts):

1. Install dsh:

   ```powershell
   npm install -g @deepseek-ai/dsh
   ```

2. Run `DshWeb.exe`. On first launch it starts the dsh service automatically and opens the UI. If it prompts for .NET Desktop Runtime, run `winget install Microsoft.DotNet.DesktopRuntime.10` first (see FAQ)

3. (Optional) Autostart: copy `start-dsh.vbs` into the Startup folder (press `Win+R`, type `shell:startup`, Enter)

4. (Optional) Desktop shortcut: right-click `DshWeb.exe` → Send to → Desktop (create shortcut)

5. (Uninstall) run `uninstall-autostart.cmd` to remove the autostart entry and desktop shortcuts

#### Option 2: build from source

```powershell
git clone https://github.com/Ruler4396/dsh-launcher.git
cd dsh-launcher
./scripts/build-release.ps1    # one-shot packaging, produces dist\dsh-launcher-windows.zip (all scripts included)
```

Or publish manually (remember to copy the deployment scripts into the output directory):

```powershell
dotnet publish src/DshShell -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o dist
copy scripts\start-dsh.vbs, scripts\dsh-web.cmd, scripts\uninstall-autostart.cmd dist\
```

The build produces `dist\DshWeb.exe` (framework-dependent single file, ~1MB, needs .NET Desktop Runtime 10); the rest is the same as Option 1.

### Directory layout

```
dsh-launcher/
├── README.md
├── LICENSE
├── scripts/                 # deployment scripts (all shipped in the release, next to DshWeb.exe)
│   ├── start-dsh.vbs        # silent windowless service launcher (autostart / shell both use it)
│   ├── start-dsh.cmd        # foreground debug start (log window)
│   ├── dsh-web.cmd          # one-click entry: check port → start service → open shell
│   ├── uninstall-autostart.cmd  # remove autostart entry and desktop shortcuts
│   └── build-release.ps1    # packaging script (development only, not shipped)
└── src/
    └── DshShell/            # shell app source (C# WinForms + WebView2)
        ├── DshShell.csproj
        └── Program.cs
```

### How it works

| Module | Approach |
| --- | --- |
| Shell app | WinForms + `Microsoft.Web.WebView2`, single-file `PublishSingleFile` build |
| Silent start | VBS runs `dsh web --host 127.0.0.1 --port 3080` in the background via `wscript`, output redirected to the log |
| Port probe | `TcpClient.Connect("127.0.0.1", 3080)` on shell startup; polls until ready (up to 90s) |
| Autostart | `start-dsh.vbs` placed in the Startup folder, executed windowless by `wscript` at logon, no admin rights |
| Notification permission | WebView2 `PermissionRequested` auto-grants notifications and clipboard (for dsh-notification and similar plugins); microphone/camera stay denied by default |
| Downloads | files are saved to the system Downloads folder (collision-free names) and opened with the default handler on completion |
| External links | `window.open()` targets outside the app open in the system browser; internal popups stay in the shell |
| Crash recovery | a crashed/unresponsive render process is auto-reloaded (10s throttle to avoid loops) |
| Single instance | a second launch just focuses the existing window instead of spawning another WebView2 process |
| Port | `3080` by default, matching dsh; to change it, update `start-dsh.vbs`, `dsh-web.cmd` and `Program.cs` together |

### Version compatibility

- **Small dependency surface** — only uses the `dsh web` CLI (`--host` / `--port`), the default port `3080`, and the Web UI over HTTP; no dependence on dsh internals, so dsh upgrades usually do not require rebuilding the shell
- **Upgrading** — run `npm update -g @deepseek-ai/dsh` and restart the service; the shell app does not need rebuilding
- **Note** — dsh is in developer preview and the official docs warn about possible breaking changes. If the launch arguments or the default port change, update `start-dsh.vbs`, `dsh-web.cmd` and `Program.cs` in sync
- This tool never pins or modifies the dsh version; it always follows the latest version installed locally

### FAQ

**Q: The page does not open after startup?**
Check `%USERPROFILE%\.dsh-web.log`, confirm dsh is installed (`dsh --version`), and verify that port 3080 is not occupied.

**Q: What if port 3080 is already in use?**
Change the port in `start-dsh.vbs`, `dsh-web.cmd` and `Program.cs`, then rebuild/redeploy.

**Q: It asks to install .NET Desktop Runtime?**
The shell app is framework-dependent and needs .NET Desktop Runtime 10. Install it with `winget install Microsoft.DotNet.DesktopRuntime.10`, or download it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0), then retry.

**Q: How do I uninstall completely?**
Run `uninstall-autostart.cmd` from the deploy folder (it removes the autostart entry and desktop shortcuts), then delete the whole deploy folder.

**Q: Why not Electron / Tauri?**
Electron bundles a full Chromium (same memory cost as a browser); Tauri also uses WebView2 underneath but needs a Rust toolchain. This project wraps WebView2 directly, which keeps the artifact smaller and the build simpler.

### Disclaimer

This repository is an **independent third-party tool** and is not affiliated with DeepSeek / DeepSeek AI. [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) (`dsh`) is the official project under its own license (MIT). This tool only wraps dsh at the local-launch and display layer; it contains no official code and does not alter official behavior.

The window icon uses the DeepSeek brand logo (`favicon.png`), owned by DeepSeek. It is used for personal, local use only; do not distribute it separately in commercial releases.

### License

[MIT](LICENSE) © dsh-launcher contributors
