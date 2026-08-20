# 后台更新安装与前台 UI 状态联动修复

> 目标：重启应用更新时，SplashForm 实时显示"正在应用更新 (vX)…"与 npm 实时安装日志，
> 修正"下次启动自动应用"误导文案，安装失败给用户明确反馈。测试全绿 379/379。

---

## 一、任务一：状态机与 UI 联动

### 1.1 进度链路（Program 桥接 → Splash）

```
SplashForm → RunLauncherAppPipelineAsync
  ├─ textProgress(IProgress<string>)
  │    └─ 桥接成 _updateApplyProgress（"正在应用更新…" + npm 实时日志）
  └─ RunStartupAsync → 阶段0 BackgroundMaintenance(ct, _updateApplyProgress)
       └─ HandlePendingUpdateAtStartup → ApplyPendingDshUpdate(ct, progress)
            ├─ progress?.Invoke("正在应用更新 (vX)…")
            ├─ RunNpmCommand(..., progress) → npm 逐行实时日志滚动上报
            └─ progress?.Invoke("更新 vX 已应用完成。")
```

### 1.2 Splash 文本 + 取消按钮管控（`SplashForm.cs`）

- `Message` 新增 `bool IsApplyingUpdate` 标志。
- `[apply]` 前缀 → `IsApplyingUpdate: true` → Label 显示进度、**取消按钮禁用**（灰显"安装中…"），
  防止用户在 npm install 中途强杀导致 node_modules 损坏。
- 收到非 apply 消息（更新结束/下一阶段）→ 恢复取消按钮。

### 1.3 npm 实时日志（`Program.RunNpmCommand`）

- 从 `ReadToEndAsync`（一次性读全）改为 `BeginOutputReadLine`/`BeginErrorReadLine` 逐行事件，
  每行实时 `progress?.Invoke(line)`（如 "added 50 packages"）滚动显示到 Splash。
- 同时收集 stdout+stderr 用于 `errorTail` 诊断（失败弹窗展示）。

---

## 二、任务二：文案修正（中英对照）

| 位置 | 旧文案 | 新文案 |
|---|---|---|
| 下载完成弹窗 `DownloadDshUpdateStaged` | "dsh {v} 已下载完成，将在下次服务启动时自动应用（即关闭本窗口后，不会打断当前会话）。" | "更新 dsh {v} 已下载完成。下次重启启动器时将自动安装（预计需要 1-2 分钟，期间请耐心等待）。" |
| 待应用气泡 `NotifyPendingApply` | "已下载 dsh {v}，将在下次服务启动时自动应用（关闭本窗口后生效；或手动执行：npm install -g @deepseek-ai/dsh@{v}）。" | "更新 dsh {v} 已下载完成。下次重启启动器时将自动安装（预计需要 1-2 分钟，期间请耐心等待）。" |
| 询问弹窗 `PromptDshUpdate` | "是否在后台下载并安排更新？\n（下载完成不打扰当前会话；将在下次服务启动时自动应用，即关闭本窗口后）" | "是否在后台下载并安排更新？\n（下载不打扰当前会话；下次重启启动器时将自动安装，预计需要 1-2 分钟，期间请耐心等待）" |
| 更新安装 Splash | （无，阶段 0 静默） | "正在应用更新 (vX)…" + npm 实时日志 + 取消按钮禁用 |

---

## 三、任务三：安装失败 UI 反馈

### 3.1 弹窗（`NotifyUpdateApplyFailed`）

非重试失败（权限/包损坏）时，显示主窗**前**弹模态框：
```
自动应用更新失败 (vX.X.X)。
将继续使用旧版本启动。
原因：<具体错误/超时>
您可以稍后在设置中重试更新。
```
`DSH_NO_UI` 测试钩子下仅写日志不弹窗。

### 3.2 pending 保留/清理策略（`ShellLogic.IsRetryableNpmError`）

| 错误类型 | 判定 | 处理 |
|---|---|---|
| 网络/超时类（ETIMEDOUT/ECONNRESET/ECONNREFUSED/ENOTFOUND/EAI_AGAIN/timed out/registry） | 可重试 | 保留 pending，下次启动自动重试（仅日志，不打扰） |
| 权限/包损坏类（EACCES/EINTEGRITY/ERESOLVE 等） | 不可重试 | 清 pending 防死循环 + 模态弹窗 |

---

## 四、任务四：回归测试

新增 `tests/DshShell.Tests/Managers/UpdateFlowContractTests.cs`（+15 用例，套件 364→379）：

1. `UpdateApply_ProgressReported`：Mock BackgroundMaintenance 内上报"正在应用更新"+"added 50 packages"+"updated 1 package"，断言 progress 全部收到。
2. `UpdateApply_Failure_DoesNotBlockStartup_OldVersionContinues`：Mock 更新失败上报，断言状态机仍进入 `Running`（旧版本继续启动）。
3. `UpdateApply_BackgroundMaintenance_RunsBeforeReadiness_AndUiStaysResponsive`：验证阶段 0 更新上报先于后续进度。
4. `IsRetryableNpmError_Classifies_RetryableVsFatal`：Theory 锁定 pending 保留/清理契约（网络类 true / 权限类 false / 空 false）。

运行：
```powershell
cd E:\dsh-launcher
dotnet test tests\DshShell.Tests\DshShell.Tests.csproj -c Release   # 379/379 通过
```

---

## 五、手动验证步骤（本地 Windows）

```powershell
# 1) 造一个 pending 更新（模拟已下载待应用）
#    在 DSH_HOME\dsh-launcher\pending-update.json 写入：
#    {"version":"0.1.0-rc.7","at":"...","failCount":0}
#    （或用 DSH_TEST_FAKE_APPLY=1 钩子，E2E 用）

# 2) 启动，观察 Splash
.\DshWeb.exe
# 期望：Splash Label 依次显示"正在准备启动环境…" → "正在应用更新 (v0.1.0-rc.7)…"
#      → npm 实时日志滚动 → "更新 v0.1.0-rc.7 已应用完成。" → 进入正常启动
#      更新期间"取消"按钮灰显"安装中…"

# 3) 验证失败路径（模拟权限错误）
#    pending 保留策略：修改 errorTail 使其含 "EACCES" → 清 pending + 弹窗"自动应用更新失败"
#    含 "ETIMEDOUT" → 保留 pending 仅记日志
```

---

## 六、改动文件

| 文件 | 变更 |
|---|---|
| `Program.cs` | `RunBackgroundMaintenance`/`HandlePendingUpdateAtStartup`/`ApplyPendingDshUpdate` 增 progress 参数；`RunNpmCommand` 逐行实时日志；新增 `NotifyUpdateApplyFailed` + pending 策略；`RunLauncherAppPipelineAsync` 装配 `[apply]`/`[warn]` 桥接；三处文案修正 |
| `SplashForm.cs` | `Message` 增 `IsApplyingUpdate`；更新期禁用取消按钮 + Label 切换 |
| `LauncherApp.cs` | 阶段 0 进度透传注释（组合根桥接） |
| `ShellLogic.cs` | 新增 `IsRetryableNpmError` 纯函数 |
| `UpdateFlowContractTests.cs` | 新增 4 类回归测试 |
