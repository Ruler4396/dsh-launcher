# dsh-launcher

[![build](https://github.com/Ruler4396/dsh-launcher/actions/workflows/build.yml/badge.svg)](https://github.com/Ruler4396/dsh-launcher/actions/workflows/build.yml)
[![license](https://img.shields.io/github/license/Ruler4396/dsh-launcher)](LICENSE)
[![release](https://img.shields.io/github/v/release/Ruler4396/dsh-launcher)](https://github.com/Ruler4396/dsh-launcher/releases)
[![stars](https://img.shields.io/github/stars/Ruler4396/dsh-launcher)](https://github.com/Ruler4396/dsh-launcher)

> DeepSeek Harness 的 Windows 低占用桌面启动器：开机自启 + 轻量独立窗口，摆脱手动敲命令、浏览器高内存占用。

[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（`dsh`）官方目前以 `npx @deepseek-ai/dsh web` 的方式运行，需要每次手动输入命令，并通过浏览器访问 `http://127.0.0.1:3080`。本项目提供一套 Windows 下的开箱即用方案：

- **开机自启**：登录后自动静默启动 dsh 服务，不弹任何窗口
- **一键启动**：桌面图标双击即开，服务未运行时自动拉起
- **低内存**：使用 WebView2 嵌入式内核的独立窗口替代完整浏览器（实测约 50–150MB，关窗即释放；Edge/Chrome 常驻通常 500MB+）

## 特性

- 🚀 **静默自启**：VBS 无窗口启动器 + Windows 启动文件夹，无需管理员权限即可注册开机自启
- 🪟 **轻量壳窗口**：C# WinForms + WebView2 单文件发布（约 1MB），无地址栏、无扩展、无后台常驻
- 🔌 **自动拉起**：壳应用启动时探测 3080 端口，服务未运行则自动拉起并等待就绪
- 📋 **日志落盘**：服务输出写入 `%USERPROFILE%\.dsh-web.log`，便于排查
- 🧹 **可完全卸载**：提供卸载脚本，删除自启项与桌面图标

## 内存对比

| 方案 | 平时占用 | 打开界面后 |
| --- | --- | --- |
| 浏览器访问（Edge/Chrome 常驻） | 500MB+ | 更高 |
| 本项目 | 仅 dsh 服务（Node 进程，约 100–200MB） | 壳窗口 50–150MB，关闭即释放 |

> 说明：dsh 服务本身是 Node.js 进程，无论用什么前端打开都必须常驻；本项目省去的是"完整浏览器"这部分开销。

## 环境要求

| 依赖 | 版本 | 用途 |
| --- | --- | --- |
| Windows 10/11 | — | 需要内置 WebView2 Runtime（Win11 及新版 Win10 已自带） |
| Node.js | 18+ | 运行 dsh |
| .NET Desktop Runtime | 10.0+ | 运行壳应用 `DshWeb.exe` 所需；未安装时首次启动会提示，见 FAQ |
| .NET SDK | 10.0+ | 仅从源码构建时需要 |

## 快速开始

### 方式一：直接使用编译产物（推荐）

从 [Releases](https://github.com/Ruler4396/dsh-launcher/releases) 下载 `dsh-launcher-windows.zip`，解压后（内含 `DshWeb.exe`、`WebView2Loader.dll`、`runtimes\` 及全部部署脚本）：

1. 安装 dsh：

   ```powershell
   npm install -g @deepseek-ai/dsh
   ```

2. 运行 `DshWeb.exe`，首次启动会自动拉起 dsh 服务并打开界面。若提示缺少 .NET Desktop Runtime，先执行 `winget install Microsoft.DotNet.DesktopRuntime.10`（见 FAQ）

3. （可选）开机自启：把 `start-dsh.vbs` 复制到启动文件夹（`Win+R` 输入 `shell:startup` 回车）

4. （可选）桌面快捷方式：右键 `DshWeb.exe` → 发送到 → 桌面快捷方式

5. （卸载）运行 `uninstall-autostart.cmd` 删除自启项与桌面快捷方式

### 方式二：从源码构建

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

## 目录结构

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

## 技术实现

| 模块 | 方案 |
| --- | --- |
| 壳应用 | WinForms + `Microsoft.Web.WebView2`，`PublishSingleFile` 单文件发布 |
| 静默启动 | VBS 调用 `wscript` 后台运行 `dsh web --host 127.0.0.1 --port 3080`，输出重定向到日志 |
| 端口探测 | `TcpClient.Connect("127.0.0.1", 3080)`，壳启动时探测、未就绪则轮询等待（最长 90s） |
| 开机自启 | 启动文件夹放置 `start-dsh.vbs`，登录时由 `wscript` 无窗口执行，无需管理员权限 |
| 端口 | 默认 `3080`，与 dsh 默认一致；如需修改，同步改 `start-dsh.vbs`、`dsh-web.cmd`、`Program.cs` 三处 |

## 版本兼容性

- **依赖面小**：本工具只调用 `dsh web` 的 CLI 接口（`--host` / `--port`）、默认端口 `3080` 和 Web UI 的 HTTP 访问，不依赖 dsh 内部实现，因此 dsh 升级一般无需重新编译壳应用
- **升级方式**：`npm update -g @deepseek-ai/dsh` 后重启服务即可；壳应用无需重新编译
- **注意事项**：dsh 目前处于开发者预览阶段（官方声明未来可能出现破坏兼容性的变更）。若官方变更启动参数或默认端口，只需同步修改 `start-dsh.vbs`、`dsh-web.cmd`、`Program.cs` 三处即可
- 本工具不会修改或锁定 dsh 版本，始终跟随你本地安装的最新版

## 常见问题

**Q：启动后页面打不开？**
查看 `%USERPROFILE%\.dsh-web.log`，确认 dsh 是否安装（`dsh --version`）以及端口是否被占用。

**Q：端口 3080 被占用怎么办？**
修改 `start-dsh.vbs`、`dsh-web.cmd`、`Program.cs` 中的端口号后重新构建/部署。

**Q：提示需要安装 .NET Desktop Runtime？**
壳应用是框架依赖发布，运行需要 .NET Desktop Runtime 10。安装：`winget install Microsoft.DotNet.DesktopRuntime.10`，或从 [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0) 下载安装后重试。

**Q：如何彻底卸载？**
运行部署目录下的 `uninstall-autostart.cmd`（自动删除自启项与桌面快捷方式），最后删除整个部署目录即可。

**Q：为什么不用 Electron / Tauri？**
Electron 自带完整 Chromium（与浏览器同级的内存开销）；Tauri 底层同样是 WebView2 但需要 Rust 工具链。本项目直接用 WebView2 封装，产物更小、构建更简单。

## 免责声明

本仓库是**独立的第三方工具**，与 DeepSeek / DeepSeek AI 官方无关。[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（`dsh`）是官方项目，遵循其自身协议（MIT）。本项目仅在其之上做本地启动与展示层面的封装，不包含官方代码，也不修改官方行为。

窗口图标使用了 DeepSeek 品牌标识（`favicon.png`），该标识的版权归 DeepSeek 所有，仅作个人本地使用，请勿在商业发行物中单独使用。

## 许可证

[MIT](LICENSE) © dsh-launcher contributors

---

## English

`dsh-launcher` is a lightweight Windows launcher for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness):

- Auto-starts the `dsh web` service silently at logon (no admin rights required)
- Opens the Web UI in a minimal WebView2 window instead of a full browser (50–150MB vs 500MB+)
- Single-file ~1MB shell app (C# WinForms), auto-starts the service if it is not running

Requirements: Windows 10/11 with WebView2 Runtime, Node.js 18+, and .NET Desktop Runtime 10 to run the shell app (the .NET SDK is only needed to build from source).

This is an unofficial third-party tool, not affiliated with DeepSeek AI.
