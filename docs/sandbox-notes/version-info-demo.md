# sandbox/version-info-demo（沙盒场景：标题栏 dsh 版本徽标演示/调试）

> 遵循仓库核心约束六（单一沙盒根 `sandbox/`）：本目录用于在**不触碰真实环境**的前提下，
> 拉起一个独立 dsh-launcher 窗口，验证 2026-09 新增的"标题栏 dsh 版本徽标 + 点击弹出
> 版本信息窗"功能。`.gitignore` 已忽略，禁止 `git add -A`。

## 来源与版本

| 项 | 值 |
|---|---|
| 场景 | 版本徽标 UI 演示（DSH_WEB_PORT=3081 独立实例） |
| 壳源码 | 本仓库 `src/DshShell/`（Debug 构建，`bin\Debug\net10.0-windows\DshWeb.exe`） |
| dsh 运行时 | 机器全局 npm 安装 `@deepseek-ai/dsh` 0.1.2-rc.1（`%APPDATA%\npm\dsh.cmd`，**复用，不进库**） |
| DSH_HOME | 本目录 `home\`（壳数据 + dsh 服务 home 全隔离，dsh 尊重 `$DSH_HOME`） |

## 目录

- `home/` — DSH_HOME：壳数据（日志/窗口状态/runtimes）+ dsh 服务 profile（首次从随附模板自动初始化）
- `staging/` — 验证产物（截图等，脚本写入）
- `global/` — 本场景**不**建隔离 npm 安装根（复用机器全局 dsh；如需隔离 npm 安装，在此用 `npm --prefix` 建）

## 拉起与验证命令

```powershell
# 1) 构建（Debug）
dotnet build src\DshShell\DshShell.csproj -c Debug

# 2) 沙盒启动（端口 3081 ≠ 真实实例 3080，互斥键隔离 → 不与正在运行的实例抢窗口）
$env:DSH_SANDBOX = "1"          # 门控机器级副作用（不自启/不清残留/不写注册表）
$env:DSH_HOME   = "E:\dsh-launcher\sandbox\version-info-demo\home"
$env:DSH_WEB_PORT = "3081"
$env:DSH_TEST_INSTANCE = "1"    # 窗口标题带 "[TEST]"，单实例匹配不干扰真实实例
Start-Process "E:\dsh-launcher\src\DshShell\bin\Debug\net10.0-windows\DshWeb.exe"

# 3) 验证点
#    - 标题栏显示 "DeepSeek Harness [TEST] v0.1.2-rc.1"（徽标 = 发现层版本，**正文样式**：
#      与标题同字重同色、间隙 4px；悬停手型光标 + 下划线）
#    - 点击徽标 → 弹出 **dsh 风格版本信息窗**：无边框 + 复用自绘标题栏（鲸鱼图标 /
#      仅关闭按钮）+ 深/浅主题跟随（#202020 / #F0F0F0）：
#         dsh 组件      当前 v0.1.2-rc.1  最新 v0.1.1-rc.2  已是最新
#         dsh-launcher  当前 v0.4.3       最新 v0.4.3       已是最新
#         （启动器当前版本在开发构建回退 git tag，不再显示 SDK 默认的误导性 1.0.0）
#      + 启动器下载地址（LinkLabel 点击打开）+ DeepSeek 蓝"打开下载页"按钮
#    - 关闭窗口即停本实例服务；真实实例（3080）不受影响
```

## 验证产物（staging/）

- `main-window-v3.png` — 正文样式徽标截图（像素探针：徽标区无蓝色、标题色文字 bbox (166,13)-(204,21)）
- `version-dialog-v4.png` — dsh 风格紧凑对话框（深色主题 480x186：两行版本全渲染、URL 与按钮间距 20px）
- `capture/` — 截图 + 像素扫描小工具（PrintWindow + 指定色 bbox 扫描，`cap`/`scan` 子命令）

## 清理

直接删除本目录（沙盒 home 视为可重建基线）。占用的 3081 端口随窗口关闭由壳停服释放；
若残留，`taskkill /PID <pid> /T /F`（进程树）。