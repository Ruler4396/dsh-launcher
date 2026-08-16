# dsh-launcher

[English](README.en.md) · [简体中文](README.md)

[![build](https://github.com/Ruler4396/dsh-launcher/actions/workflows/build.yml/badge.svg)](https://github.com/Ruler4396/dsh-launcher/actions/workflows/build.yml)
[![license](https://img.shields.io/github/license/Ruler4396/dsh-launcher)](LICENSE)
[![release](https://img.shields.io/github/v/release/Ruler4396/dsh-launcher)](https://github.com/Ruler4396/dsh-launcher/releases)
[![featured on dsh-suite](https://img.shields.io/badge/featured%20on-dsh--suite-4d6bfe)](https://whyihaveyou.github.io/dsh-suite/)

> DeepSeek Harness 的 Windows 轻量启动器：开机自启 + 独立小窗口，**双击即用，不用敲命令**。

| 浅色模式 | 深色模式 |
|---|---|
| ![浅色模式](assets/screenshot-light.png) | ![深色模式](assets/screenshot-dark.png) |

## 安装

**方式一：MSI 安装包（推荐给新手）**

1. 到 [Releases](https://github.com/Ruler4396/dsh-launcher/releases) 下载 `dsh-launcher-<版本>.msi`
2. 双击安装，向导里可勾选**开机自启** / 桌面快捷方式 / 开始菜单快捷方式，也可自定义安装目录
3. 安装和卸载会弹一次 **UAC 管理员确认**（系统级安装，默认装到 `%ProgramFiles%\dsh-launcher`）
4. 卸载：设置 → 应用 → dsh-launcher → 卸载（或开始菜单"卸载 dsh-launcher"）

**方式二：便携版 ZIP**

1. 下载 `dsh-launcher-windows.zip`，解压到任意文件夹
2. 双击 `DshWeb.exe` 即可
3. 卸载：删掉文件夹（自启/快捷方式用 `uninstall-autostart.cmd` 清理）

## 环境要求

| 依赖 | 说明 | 怎么检查/安装 |
|---|---|---|
| **Node.js 18+** | dsh 服务运行必需（dsh 不必全局安装，启动器会自动用 `npx` 拉取） | https://nodejs.org 下载 LTS 版，默认安装 |
| **.NET Desktop Runtime 10** | 壳程序运行必需 | 缺失时双击无反应，见下方排障；`winget install Microsoft.DotNet.DesktopRuntime.10` |
| **WebView2 Runtime** | 窗口渲染用 | Windows 10/11 通常已自带；缺失时启动会自动静默安装（失败才弹 E1006） |

## 特性

- 🚀 **开机自启**：登录后自动打开启动器窗口（壳再自行拉起 dsh 服务并等待就绪），无需手动双击
- 🪟 **轻量窗口**：WebView2 独立窗口（约 50–150MB，关窗即释放），替代完整浏览器
- 🔌 **自动拉起**：服务没开时自动启动并等待就绪（首次运行需下载组件，有进度提示）
- 🔔 **差错提示**：缺 Node.js / 下载失败 / 端口占用等都会**明确弹窗**说明，不再静默
- 🎛️ **Node 服务驻留**：在 dsh 设置页（配套插件）切换"常驻 / 托盘驻留 / 跟随窗口"，决定关窗后 node 服务是继续跑还是跟着停（省内存）
- 🌗 **主题跟随**：窗口标题栏（自绘）、窗口/任务栏图标跟随 dsh 主题即时切换（深色/浅色），不用重启
- 📋 **统一日志**：壳启动轨迹与 dsh 服务输出合并为单一文件 `~/.dsh\dsh-launcher\dsh.log`（JSON Lines + 原始输出共存，`DSH_LOG_LEVEL` 控级别；启动异常时定位利器）

## 与 dsh 插件联动

dsh 设置页里有一个配套插件 **dsh-launcher-lifetime**（[GitHub](https://github.com/Ruler4396/dsh-launcher-lifetime) · [npm](https://www.npmjs.com/package/dsh-launcher-lifetime)），提供 **"Node 服务驻留"** 设置页：三个单选切换服务模式（跟随窗口 / 常驻 / 托盘驻留），**立即生效、不用重启**。

```powershell
dsh plugin add dsh-launcher-lifetime
```

- 模式保存在 `DSH_HOME\dsh-launcher\settings.json`（默认 `~/.dsh\dsh-launcher\settings.json`，与 dsh 生态一致），启动器在关窗/托盘退出时读取执行
- 托盘图标右键菜单保持精简（仅"退出"；窗口显示用左键单击托盘置顶），服务模式切换统一在插件的设置页里做
- 没装插件时启动器按默认"跟随窗口"模式工作，插件只是提供图形化切换入口

## v0.3.0 新特性一瞥

- **统一日志**：壳决策 + dsh 服务输出合并为 `~/.dsh\dsh-launcher\dsh.log` 一份文件（`DSH_LOG_LEVEL` 可设 `INFO`/`WARN`/`ERROR` 过滤），自动轮转（>30MB 或 >3 天）。
- **错误码 `[E####]`**：任何报错弹窗都带错误码（如 `E1001` 未检测到 Node、`E2002` 服务启动超时、`E2011` 插件缺失配置降级），与日志、诊断导出共用一套码，可直接粘贴到 Issue。
- **便携 Node 自动补齐**：缺 Node.js 时确认一下，自动下载 LTS 便携版到 `%LOCALAPPDATA%\dsh-launcher\env\node\`（不改系统环境）。
- **托盘按需显示**：默认隐藏；装了 dsh-launcher-lifetime 插件或有更新待通知时才出现，不打扰常驻用户。
- **dsh 延迟更新**：点更新气泡确认后后台下载，**下次启动**才应用到服务，绝不打断当前会话。
- **窗口容灾**：窗口位置/大小自动记忆；副屏拔掉等越界时回退主屏居中。
- **一键诊断导出**：命令 `DshWeb.exe --diagnose`（可加 `--min-level warn|error`）把脱敏日志/环境/错误码打包 zip 到"下载"文件夹，反馈时一键携带。
- **卸载数据边界**：MSI 卸载**自动**清理启动器自有数据（`~/.dsh\dsh-launcher\` 与旧 `.dsh-web*.log`）；便携版需显式 `uninstall-autostart.cmd -CleanData` 才清。两者都**绝不触碰** `profiles/`、`settings.yaml`、插件等 dsh 生态数据。

**v0.3.1 补强**：
- **WebView2 缺失自动修复**：若 WebView2 Runtime 缺失，启动会静默下载官方 Bootstrapper 自动安装后重试，实在装不上才报 E1006。
- **安装时自动装 .NET 与 Node 便携升级**：MSI 向导前置检查在缺 .NET 时可一键点「自动安装(A)」跑 winget 装好继续；便携 Node 默认升级到 LTS v24。

## 启动不了？按现象排查

> 大多数"没反应"都出在**环境依赖**上。先对照上方"环境要求"确认 Node.js 和 .NET 都装了，再按现象查。

**现象 1：双击后完全没反应（没有窗口、没有弹窗）**

多半是缺少 **.NET Desktop Runtime 10**。在 PowerShell 运行：

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10
```

装完再双击。仍不行 → 看下方"看日志"。

> 提示：**MSI 安装时**的前置检查已在缺 .NET 时提供「自动安装(A)」按钮，一键 winget 装好后继续，无需手动进 PowerShell。
>
> 提示：**便携版 zip** 解压目录里附带 `check-prereq.cmd`（纯 cmd、零依赖），双击运行即可一次检测 .NET / WebView2 / Node 三项前置，缺失时直接给出安装命令与下载链接——双击 exe 无反应时先用它定位。

**现象 2：弹出"未检测到 Node.js，无法启动 dsh 服务"**

v0.3.0 起会弹一次性确认框，**自动下载便携版 Node.js**（LTS，SHA256 校验 + 镜像回退，装到 `%LOCALAPPDATA%\dsh-launcher\env\node\`，不改系统环境），确认即可自动补齐；想手装同效，可安装 [Node.js](https://nodejs.org) 18+（LTS 版，一路默认下一步即可），装完**重新打开** dsh-launcher。

**现象 3：卡在"正在启动 dsh 服务…首次运行需要下载组件"很久**

第一次使用会自动下载 dsh 组件（通过 npx），**可能几分钟**，取决于网络速度。耐心等：
- 正常下载完 → 自动进入窗口
- 超过 3 分钟 → 会弹窗提示原因（下载慢 / 网络问题），并附日志尾部

网络较慢可配置 npm 国内镜像后重试：

```powershell
npm config set registry https://registry.npmmirror.com
```

**现象 4：弹出"dsh 服务未能就绪"或"dsh 服务不可用"**

打开日志看最后几行：`~/.dsh\dsh-launcher\dsh.log`（记事本打开即可）

- 日志里有 `npm ERR` 或网络相关报错 → **网络/代理问题**，重试或换网络
- 日志里有 `'npx' 不是内部或外部命令` → **Node.js 没装好**，重装 Node.js（现象 2）
- 日志里有 `EADDRINUSE` → **端口 3080 被占用**，见现象 5

还查不出来 → 日志里的 **JSON Lines 行**（`level/info`）会记录单实例、端口探测、服务拉起、就绪判定、窗口显示等每个决策点，报错弹窗都带 `[E####]` 错误码（便于在 Issue 里精确定位）；反馈问题时附上 `~/.dsh\dsh-launcher\dsh.log`。

**现象 5：端口 3080 被其他程序占用**

设置环境变量 `DSH_WEB_PORT` 换端口后重启，壳会自动按新端口拉起 dsh 服务（最简单，推荐）：

```powershell
$env:DSH_WEB_PORT = "3090"
```

如果服务是你自己手动起的，也可以用 `DSH_WEB_URL` 指向它（壳不再自动拉起服务，细节见 [docs/DETAILS.md](docs/DETAILS.md)）：

```powershell
$env:DSH_WEB_URL = "http://127.0.0.1:3090"
```

**看日志**：

```powershell
Get-Content "$env:USERPROFILE\.dsh\dsh-launcher\dsh.log" -Tail 30
```

日志第一行会写明本次是**用全局 dsh 还是 npx 回退**启动的（`[start-dsh] using ...`），JSON Lines 行则记录壳的每个决策点，方便判断问题出在哪一环。

## 常见问题

**Q：MSI 和 ZIP 有什么区别？**
内容完全相同。MSI 有标准安装/卸载流程（推荐新手）；ZIP 免安装（便携党）。

**Q：能自定义安装目录吗？卸载会误删同目录文件吗？**
MSI 向导可自定义目录；卸载只删本应用文件，目录非空则保留（已实测验证）。

**Q：卸载会清理我的日志/配置吗？会误删 dsh 数据吗？**
MSI 卸载会**自动**清理启动器自有数据（`~/.dsh\dsh-launcher\` 与旧 `.dsh-web*.log`）；便携版需显式运行 `uninstall-autostart.cmd -CleanData` 才清理。两者都**绝不触碰** dsh 生态数据（`profiles/`、`settings.yaml`、`.credentials.yaml`、插件、sessions 等）——卸载或清理后，手动装过的 dsh 及其数据原样保留。

**Q：dsh 服务一直占内存（几百 MB）？**
dsh 是完整服务（含 Web UI），常驻是设计（秒开）。想要省内存：dsh 设置页 → **"Node 服务驻留"** → **跟随窗口**（关窗即停服务，下次启动自动拉起）。

## 更多

- 技术实现 / 安全 / 发版策略 / 从源码构建：[docs/DETAILS.md](docs/DETAILS.md)
- 更新日志：[CHANGELOG.md](CHANGELOG.md)

## 免责声明

本仓库是**独立的第三方工具**，与 DeepSeek / DeepSeek AI 官方无关。[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（`dsh`）是官方项目（MIT）。窗口图标使用了 DeepSeek 品牌标识，版权归 DeepSeek 所有，仅作个人本地使用。

## 许可证

[MIT](LICENSE) © dsh-launcher contributors
