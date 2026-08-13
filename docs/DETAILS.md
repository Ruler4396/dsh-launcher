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
| 端口探测 | `TcpClient.Connect("127.0.0.1", <端口>)`，壳启动时探测、未就绪则轮询等待（最长 90s）；目标默认 `3080`，可用环境变量 `DSH_WEB_URL` 覆盖（免重建），设置后视为外部托管服务、不再自动拉起 |
| 开机自启 | MSI：`HKCU\...\Run` 一项；便携版：启动文件夹放置 `start-dsh.vbs`，均由 `wscript` 无窗口执行 |
| 权限 | `PermissionRequested` 自动放行：通知、剪贴板、多文件下载、持久存储（插件兼容），麦克风/摄像头保持默认拒绝；自动播放经共享 WebView2 环境注入的 `--autoplay-policy=no-user-gesture-required` 放行（当前 SDK 不会为 Autoplay 触发权限事件，只能走浏览器参数） |
| 下载 | 保存到系统"下载"文件夹（同名自动改名），blob: 按 MIME 补扩展名，完成后默认程序打开 |
| 弹窗 | 外部 http(s) → 系统默认浏览器；同源弹窗新建轻量窗口（保留会话）；blob:/data: 保持默认 |
| 崩溃自愈 | 渲染进程崩溃/无响应自动重载（10 秒节流） |
| 单实例 | 按目标端口隔离的互斥锁：重复启动自动聚焦已开窗口，不重复创建 WebView2 进程 |
| 安装包 | WiX v5 per-user MSI：无管理员、无服务、无计划任务，可卸载 |

## 安全说明 / Security

- **per-user 安装，无需管理员权限**：MSI 安装到 `%LOCALAPPDATA%\dsh-launcher`，不写 Program Files，不注册服务、不创建计划任务；卸载零残留
- **自启仅当前用户**：`HKCU\...\Run` 一个注册表值，卸载/`uninstall-autostart.cmd` 时自动删除
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

```powershell
git clone https://github.com/Ruler4396/dsh-launcher.git
cd dsh-launcher
./scripts/build-release.ps1    # zip + MSI 安装包 + SHA256 校验和；版本默认取最近 git tag
```

或手动 publish（记得把 scripts 下的部署脚本一并放入产物目录）：

```powershell
dotnet publish src/DshShell -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o dist
copy scripts\start-dsh.vbs, scripts\dsh-web.cmd, scripts\uninstall-autostart.cmd dist\
```

构建产物：`dist\DshWeb.exe`（框架依赖单文件，约 1MB，需 .NET Desktop Runtime 10）、`dist\dsh-launcher-<版本>.msi`、`dist\SHA256SUMS.txt`。

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
│   ├── product.wxs        # WiX v5 源文件：per-user MSI（向导选择开机自启）
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
2. 插件默认"仅在后台时通知"：窗口最小化/被遮挡时才弹；想看着窗口也弹就关掉该开关
3. 页面必须保持打开（可后台）；连接中断期间完成的回合不会补发
4. 仍不生效：F12 → Console 过滤 `dsh-notification`，看 `show=false` 时括号里的原因（`permission=` / `backgroundOnly=` / `hidden=`）

**Q：MSI 和 ZIP 有什么区别？**
见 [Releases](https://github.com/Ruler4396/dsh-launcher/releases) 页面的"安装与卸载"说明。
