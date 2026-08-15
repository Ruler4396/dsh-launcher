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

| 模块 | 方案 |
| --- | --- |
| 壳应用 | WinForms + `Microsoft.Web.WebView2`，`PublishSingleFile` 单文件发布 |
| 静默启动 | VBS 调用 `wscript` 后台运行 `dsh web --host 127.0.0.1 --port 3080`，输出重定向到日志；`dsh` 不在 PATH 时自动回退 `npx -y @deepseek-ai/dsh web` |
| 端口探测 | `TcpClient.Connect("127.0.0.1", <端口>)`，壳启动时探测、未就绪则轮询等待（最长 180s）；目标默认 `3080`，可用环境变量 `DSH_WEB_URL` 覆盖（免重建），设置后视为外部托管服务、不再自动拉起 |
| 开机自启 | MSI 勾选后由壳首次启动写入 `HKCU\...\Run`（安装器只落 HKLM 意图标志——per-machine 提权安装直接写 HKCU 不可靠）；便携版：启动文件夹放置 `start-dsh.vbs`，均由 `wscript` 无窗口执行 |
| 权限 | `PermissionRequested` 自动放行：通知、剪贴板、多文件下载、持久存储（插件兼容），麦克风/摄像头保持默认拒绝；自动播放经共享 WebView2 环境注入的 `--autoplay-policy=no-user-gesture-required` 放行（当前 SDK 不会为 Autoplay 触发权限事件，只能走浏览器参数） |
| 下载 | 保存到系统"下载"文件夹（同名自动改名），blob: 按 MIME 补扩展名，完成后默认程序打开 |
| 弹窗 | 外部 http(s) → 系统默认浏览器；同源弹窗新建轻量窗口（保留会话）；blob:/data: 保持默认 |
| 崩溃自愈 | 渲染进程崩溃/无响应自动重载（10 秒节流） |
| 单实例 | 按目标端口隔离的互斥锁：重复启动自动聚焦已开窗口，不重复创建 WebView2 进程 |
| 安装包 | WiX v5 per-machine MSI（安装/卸载需 UAC 提权）：默认 `%ProgramFiles%\dsh-launcher` 可自定义，无服务、无计划任务，可卸载 |

## 安全说明 / Security

- **系统级安装（per-machine，提权）**：安装/卸载会弹一次 UAC 管理员确认，默认安装到 `%ProgramFiles%\dsh-launcher`，向导中可自定义安装目录；不注册服务、不创建计划任务。提权同时是卸载零报错的保证：Windows Installer 在卸载期对安装盘根 `Config.Msi` 里的回滚文件（.rbf）以用户身份设置安全，而该目录 ACL 硬编码为仅 SYSTEM/管理员，非提权在 ACL 异常的磁盘（如 E:\）必报 1926（详见 FAQ）
- **卸载只删自己的文件**：MSI 卸载仅移除本应用安装的文件；目录只会在"空"时才被删除，预先存在的文件（如与 DeepSeek Harness 共用目录）绝不会被误删（已实测验证）
- **自启仅当前用户**：安装器写机器级意图标志（`HKLM\Software\dsh-launcher\AutoStartWanted`，随卸载自动清除），壳首次启动时以当前用户身份落地 `HKCU\...\Run` 一个注册表值；卸载或 `uninstall-autostart.cmd` 时自动删除（脚本同时清除意图标志，防止壳自愈复活）
- **下载校验**：每次 Release 附带 `SHA256SUMS.txt`
- **代码签名**：安装包当前未签名，SmartScreen 可能提示"未知发布者"（正常）；正式分发建议购买代码签名证书
- **数据本地化**：WebView2 数据在 `%LOCALAPPDATA%\DshWeb`，日志在 `%USERPROFILE%\.dsh-web.log`，无遥测

## 发版策略 / Release policy

- **严重问题/安全修补** → 立即打补丁版本 tag（`vX.Y.Z+1`）发版，CHANGELOG 同步更新
- **新功能** → 升次版本号发版
- 每次 tag 推送，CI 自动：跑单测 → 构建 zip + MSI + SHA256 校验和 → 从 CHANGELOG 生成 Release 说明并发布

## 版本兼容性 / Version compatibility

- 只调用 `dsh web` 的 CLI（`--host` / `--port`）、默认端口 `3080` 和 Web UI 的 HTTP 访问，不依赖 dsh 内部实现，dsh 升级一般无需重新编译壳；壳的目标地址可用环境变量 `DSH_WEB_URL` 覆盖（默认 `http://127.0.0.1:3080`）
- `npm update -g @deepseek-ai/dsh` 后重启服务即可
- dsh 处于开发者预览阶段，若官方变更启动参数或默认端口：壳侧设置 `DSH_WEB_URL` 即可免重建；自启脚本需同步修改 `start-dsh.vbs`、`dsh-web.cmd` 两处
- 本工具不锁定 dsh 版本，始终跟随本地最新版

## 从源码构建 / Building from source

**方式一：完整发布（zip + MSI 安装包）**，需要 [WiX v5](https://wixtoolset.org/)：

```powershell
git clone https://github.com/Ruler4396/dsh-launcher.git
cd dsh-launcher
dotnet tool install --global wix --version 5.0.2   # 一次性
./scripts/build-release.ps1 -Version 0.1.8          # zip + MSI + SHA256
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
├── README.md
├── docs/DETAILS.md        # 本文件
├── LICENSE
├── assets/                # README 截图
├── installer/
│   ├── product.wxs        # WiX v5 源文件：per-machine MSI（向导选择开机自启/快捷方式/目录）
│   └── License.rtf        # 安装向导许可页
├── scripts/               # 部署脚本（发布包内含全部脚本，与 DshWeb.exe 同目录）
│   ├── start-dsh.vbs      # 无窗口静默启动服务（自启/壳拉起共用）
│   ├── start-dsh.cmd      # 前台调试启动（带日志窗口）
│   ├── dsh-web.cmd        # 一键入口：检查端口 → 拉起服务 → 打开壳
│   ├── uninstall-autostart.cmd  # 删除自启项与桌面快捷方式
│   ├── test.ps1           # 集成测试
│   └── build-release.ps1  # 打包脚本（仅开发用，不随发布包分发）
├── src/
│   └── DshShell/          # 轻量壳应用源码（C# WinForms + WebView2）
│       ├── DshShell.csproj
│       ├── Program.cs
│       └── ShellLogic.cs
└── tests/
    └── DshShell.Tests/    # 单元测试
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

**Q：升级后有两个 dsh-launcher（设置 → 应用里出现旧版本条目）？**
0.1.5 及更早是 per-user 安装（注册在 HKCU），新版是 per-machine（HKLM），MSI 的跨作用域升级在标准机器上找不到旧版，因此可能并存。**新版壳启动时会自动检测并提示**："检测到旧版本的 dsh-launcher，是否现在卸载？"——点"是"即以管理员方式卸载旧版（提权卸载不会触发 1926），点"否"则不再打扰（之后可随时手动卸载）。当前运行的版本通过安装时写入的 `HKLM\Software\dsh-launcher\CurrentProductCode` 识别，永远不会被误卸载。
