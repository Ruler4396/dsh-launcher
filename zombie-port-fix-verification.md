# 僵尸端口 / 日志锁死修复验证清单（任务一~四）

> 目标：复现「卡在等待服务就绪 180s 超时 + 日志静默丢失」生产 Bug，验证三重验证 / 僵尸清理 / Logger fallback 修复生效。

---

## 一、代码变更摘要

| 文件 | 变更 |
|---|---|
| `src/DshShell/ShellLogic.cs` | 新增 `ServicePortState` 枚举、`GetProcessIdByPort`（P/Invoke `GetExtendedTcpTable` + netstat 回退）、`KillProcessTree`（`taskkill /T /F`）、`GetAncestorPids`（toolhelp 快照） |
| `src/DshShell/Managers/ServiceManager.cs` | 新增 `ProbePort`（TCP + 进程身份 + 快速 HTTP 三重验证）与 `KillZombieTree`（杀 node + 祖先 cmd/npx 外壳 + 等待端口释放） |
| `src/DshShell/Managers/ManagerInterfaces.cs` | `IServiceManager` 新增 `ProbePort` / `KillZombieTree` 契约 |
| `src/DshShell/LauncherApp.cs` | StartingService 决策树：Healthy→跳过、Zombie→清理重启、Foreign→E2004 快速失败、Closed→拉起；fallback 告警上报 |
| `src/DshShell/Logger.cs` | `FileStream` + `FileShare.ReadWrite` 替代 `File.AppendAllText`；IOException/UnauthorizedAccess → 写 `%TEMP%\dsh-launcher-fallback-{pid}.log` + `Console.Error` 告警；`FallbackUsed`/`ResetForTest` |
| `src/DshShell/Windows/SplashForm.cs` | `Message` 新增 `IsWarn`（黄色告警） |
| `src/DshShell/Program.cs` | 桥接层 `[warn]` 前缀 → 黄色；`WaitServiceReady` 日志错误标志检查包含 fallback 日志 |

---

## 二、自动化回归测试（已通过，364/364）

```powershell
cd E:\dsh-launcher
dotnet test tests\DshShell.Tests\DshShell.Tests.csproj -c Release
```

新增测试：
- `ServiceManagerTests.ProbePort_WhenPortClosed_ReturnsClosed`
- `ServiceManagerTests.ProbePort_PortOpenAndHttpReady_ReturnsHealthy`
- `ServiceManagerTests.ProbePort_PortOpenButHttpFails_NodeOwner_ReturnsZombie`
- `ServiceManagerTests.ProbePort_PortOpenButHttpFails_NonNodeOwner_ReturnsForeign`
- `ServiceManagerTests.ZombieCleanup_PortOccupiedButHttpFails_KillsProcessTree`
- `ServiceManagerTests.KillZombieTree_WhenPortAlreadyReleased_ReturnsTrueWithoutKill`
- `LauncherAppScenarioTests.ZombiePort_KillSucceeds_StartServiceInvoked_ThenReadiness`
- `LauncherAppScenarioTests.ZombiePort_KillFails_TransitionsToFailed_WithE2004_NotTimedOut`
- `LauncherAppScenarioTests.ForeignPort_OccupiedByOtherProgram_TransitionsToFailed_WithE2004`
- `LoggerTests.Logger_Lock_Fallback_MainLockedByFileShareNone`
- `LoggerTests.WriteWhenPathBlocked_FallsBackToTemp_NotSilentlyLost`

---

## 三、手动复现（制造僵尸环境）

### 复现 1：僵尸 node 占用端口（HTTP 死）

```powershell
# 1) 手工制造"僵尸服务"：一个只监听不响应 HTTP 的 node 进程
#    （npx 下载挂起 / 半启动的 dsh 均可模拟；这里用 node 裸监听）
node -e "require('net').createServer().listen(3080, '127.0.0.1'); setInterval(()=>{},1000)"
# 2) 再套一层 cmd 外壳（模拟 start-dsh.vbs 的 cmd /c 包装 + 锁日志）
cmd /c "echo [start-dsh] fake zombie >> %USERPROFILE%\.dsh\dsh-launcher\dsh.log && timeout /t 3600"
```

### 复现 2：日志被独占锁死

```powershell
# 用另一个进程以 FileShare.None 打开 dsh.log（模拟极端独占）
# PowerShell 5.1 单行验证：
$fs = [System.IO.File]::Open("$env:USERPROFILE\.dsh\dsh-launcher\dsh.log", 'Open', 'ReadWrite', 'None')
# 保持打开，然后启动 DshWeb.exe → 观察 fallback
```

### 预期行为（修复后）

| 场景 | 修复前 | 修复后 |
|---|---|---|
| 僵尸 node 占 3080 | 跳过拉起 → 傻等 180s → E2002 | Splash 显示"检测到残留的 dsh 服务，正在清理并重新启动…" → taskkill 僵尸树 → 重新拉起 → 正常就绪 |
| 端口被非 dsh 程序占用 | 傻等 180s → E2002 | 数秒内快速失败 E2004，弹窗提示端口冲突（含 PID） |
| dsh.log 被锁 | 日志静默丢失，无法诊断 | 启动窗黄色告警"日志文件被占用，部分日志已写入临时目录：…"；`%TEMP%\dsh-launcher-fallback-{pid}.log` 有完整日志；`Console.Error` 输出 `[FATAL LOGGER] ...` |

---

## 四、端到端手动验证步骤（本地 Windows）

```powershell
# 0) 前置：确保用最新构建产物
dotnet publish src\DshShell -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .publish-tmp
#    或直接运行 bin\Release\net10.0-windows\win-x64\DshWeb.exe（脚本已自动复制到输出目录）

# 1) 复现僵尸场景
node -e "require('net').createServer().listen(3080,'127.0.0.1');setInterval(()=>{},1000)"
.\DshWeb.exe
#   → 期望：状态窗黄色/普通提示"清理残留服务" → 服务被强杀 → 重新拉起 dsh → 主窗出现

# 2) 复现日志锁
$fs = [System.IO.File]::Open("$env:USERPROFILE\.dsh\dsh-launcher\dsh.log",'Open','ReadWrite','None')
.\DshWeb.exe
#   → 期望：启动窗出现黄色"日志文件被占用…"；dsh-launcher-fallback-<pid>.log 有内容
$fs.Dispose()

# 3) 清理验证：确认无残留
tasklist | findstr /i node
netstat -ano | findstr :3080
Get-ChildItem "$env:TEMP\dsh-launcher-fallback-*.log"
```

---

## 五、已知边界与注意事项

1. `GetProcessIdByPort` 优先 `GetExtendedTcpTable`（仅 LISTENER 表），失败回退 netstat——两者都是 IPv4 TCP；IPv6 `::1` 监听不覆盖（dsh 服务固定监听 127.0.0.1，符合契约）。
2. 僵尸清理杀的是「占用端口进程的祖先链」：node 本体 + 沿途 cmd/npx 外壳。`taskkill /T /F` 本身向下杀，祖先链由 `GetAncestorPids`（toolhelp 快照，上限 8 层）补充向上清理。
3. 端口释放等待最长 2s；杀不干净 → LauncherApp 直接 E2004 快速失败（不傻等 180s），弹窗提示用户手动关闭。
4. Logger fallback 仅在 IOException / UnauthorizedAccessException 时触发；其余异常仍静默（日志失败绝不能影响启动）。
