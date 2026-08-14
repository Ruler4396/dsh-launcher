# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 变更

- **默认省内存**：未配置服务模式时默认"跟随窗口"（关窗即停 dsh 服务，下次启动自动拉起；想常驻在插件设置里改）；MSI 安装向导的**开机自启默认不勾选**，勾选框注明"内存占用相对较大，非必要不推荐开启"
- **托盘图标始终显示**（任何服务模式）：此前只在"托盘驻留"模式创建，导致默认"常驻"模式下用户找不到"服务模式"切换入口；现在启动即有托盘（小鲸鱼），右键可随时切换模式/退出（常驻模式托盘退出只退壳、服务保留）；托盘创建失败不影响壳主流程
- 托盘右键菜单瘦身：移除"服务模式"子菜单（改为插件在 Harness 设置页配置），保留显示/隐藏与退出
- 壳支持环境变量 `DSH_WEB_PORT` 指定**壳托管的服务端口**（3080 被占用时可用；`DSH_WEB_URL` 仍为外部托管语义）：壳按该端口拉起 dsh 服务（start-dsh.vbs 支持 `DSH_PORT` 透传），单实例锁、就绪探测、关窗停服务都按该端口
- 启动轨迹日志：`%LOCALAPPDATA%\dsh-launcher\shell.log` 记录壳的关键决策点（单实例、端口探测、服务拉起、就绪判定、窗口显示），启动异常时可直接查看定位

### 修复

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
