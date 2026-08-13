# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与 [语义化版本](https://semver.org/lang/zh-CN/)。

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
