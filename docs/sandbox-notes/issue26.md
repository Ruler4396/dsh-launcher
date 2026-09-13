# sandbox/issue26 — 就绪前服务进程退出盲等问题（GitHub issue #26）复现与验证

> 场景：dsh-launcher 一直停在"正在等待 dsh 服务就绪…"，命令行 `dsh web` 正常。
> 定位结论：壳拉起的 dsh 服务进程在 HTTP 就绪前退出时，PollReadiness 只观测
> TCP/HTTP + 启动错误标志词表；退出输出不含词表（如 EADDRINUSE / 引擎内部 TypeError）
> 时日志判定盲 → 盲等完整预算（180s/360s）→ 误导性 E2002。修复：进程退出观测 →
> 第五态 `service-exited` → E2010 快速失败。

## 目录/产物

- `app045/` — 从 `dist/dsh-launcher-windows-0.4.5.zip` 解压的 **0.4.5 原始二进制**
  （issue #26 用户所报版本），用于复现基线。
- `app-fixed/` — 当前源码 `dotnet publish` 的修复版（`DshWeb.exe`）。
- `home*` / `home-*` — 各实验的隔离 DSH_HOME。
- `global-old/` — pnpm 安装的 **dsh 0.1.0-rc.8**（含完整 node_modules）。
- `global-011/` — pnpm 安装的 **dsh 0.1.1-rc.2**（含完整 node_modules）。
- `boom-service.js` — 模拟"启动后立即崩溃且输出不含启动错误标志"的服务（code=7）。
- `launcher045*.out/err.log`、`launcher-fixed.*`、各 `svc*.log` — 运行取证。

## 关键复现命令（全部在沙盒内，DSH_HOME 隔离）

```powershell
# 0.4.5 原始二进制复现盲等（E2E 模式 ~20s；生产 180s）：
$env:DSH_HOME='E:\dsh-launcher\sandbox\issue26\home-boom'
$env:DSH_WEB_PORT='3964'; $env:DSH_NO_UI='1'; $env:DSH_E2E='1'
$env:DSH_SERVICE_CMD='D:\node\node.exe E:\dsh-launcher\sandbox\issue26\boom-service.js'
& 'E:\dsh-launcher\sandbox\issue26\app045\dsh-launcher-windows-0.4.5\DshWeb.exe'
# 日志末：service process exited (code=7) → poll: timeout after 180s → E2002 误导文案

# 修复版：同一场景 2.4s 内快速失败：
& 'E:\dsh-launcher\sandbox\issue26\app-fixed\DshWeb.exe'
# 日志末：poll: service process exited before ready (code=7), failing fast → E2010 真实文案

# 老 dsh 版本在 launcher 启动参数下的行为（全部可正常监听+HTTP 应答）：
node <global-old|\global-011|dsh-alpha2>\node_modules\@deepseek-ai\dsh\lib\bin.js `
  web --host 127.0.0.1 --port 3958 --no-open
```

## 版本兼容矩阵（launcher 启动参数 `web --host 127.0.0.1 --port P --no-open`）

| dsh 版本 | 结果 |
|---|---|
| 0.1.0-rc.8 | ✅ HTTP 200（无 token 栅栏） |
| 0.1.1-rc.2 | ✅ HTTP 200（无 token 栅栏） |
| 0.1.2-alpha.2 | ✅ HTTP 401（token 栅栏；IsHttpReady 任意应答视为就绪） |
| 0.1.2-rc.1 | ✅ HTTP 401（token 栅栏） |

## 修复落点（源码）

- `ServiceManager`：静态进程追踪器扩展 exit 观测（`TrackedServiceExitCodeOrMinusOne`）。
- `PollReadiness`：新增第五态 `service-exited` 快速失败（可选退出探针注入）。
- `ShellLogic.ServiceReadiness`：`ServiceExitedVerdict` / `IsServiceExitFailFast` /
  `MapVerdictErrorCode`（纯函数 + 契约测试）。
- `ErrorCodes.E2010` + `Program.HandleStartupFailure` 映射与清理分支。
- 测试：`ServiceReadinessContractTests`、`PollReadinessTests.ServiceProcessExited*`、
  `LauncherAppScenarioTests.ServiceExitedBeforeReady_*`、
  `Regression_Issue26_ServiceExitBeforeReady.RealOs`（零 Mock 真实 node 秒退）。

## 备注

- 本机环境自带 `DSH_WEB_URL=http://127.0.0.1:3080`（Harness GUI 常驻服务），
  沙盒内跑完整启动链必须显式 `Remove-Item Env:DSH_WEB_URL`，否则走外部托管分支。
- 单实例 mutex 按端口隔离（`Local\DshWeb.SingleInstance.{port}`），
  沙盒用非 3080 端口不会与宿主实例冲突。