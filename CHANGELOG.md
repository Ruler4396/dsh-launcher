# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与 [语义化版本](https://semver.org/lang/zh-CN/)。

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
