# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 新增

- **启动依赖预检**：壳在需要自动拉起 dsh 服务前快速检测 Node.js，缺失时立即弹窗提示安装（不再静默等待超时才报"服务不可用"）；WebView2 初始化失败也有明确提示（此前会静默无窗口）
- **服务启动状态窗**：自动拉起服务期间显示"正在启动 dsh 服务…首次运行需要下载组件"的进度提示（可取消）；首次 npx 下载不再是静默等待——超时（3 分钟）会区分"下载较慢/网络问题"并指引日志 `%USERPROFILE%\.dsh-web.log`

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
