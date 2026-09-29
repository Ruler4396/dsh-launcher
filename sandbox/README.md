# sandbox/ — 本地实验沙盒（`.gitignore` 已忽略，绝不入库）

> 铁律见 `docs/00-ARCHITECTURE-GUARDRAILS-MANDATORY.md` 核心约束六：一切本地测试/验证环境
> 必须落在仓库根 `sandbox/<场景名>/` 下，禁止仓库外另建沙盒。

| 目录/脚本 | 用途 | 来源（issue/现象） | 关键命令 |
|---|---|---|---|
| `ime-repro/` | 最小 WinForms + IME 组合复现器（判定崩溃触发面） | issue #28-2 版本徽标闪退 | `dotnet build -c Release` 后 `ImeRepro.exe <mode> <log>`（见其 README） |
| `tray-render/` | 反射加载生产 `DshWeb.dll`，调用真实 `TrayMenuForm.Draw` 渲染 PNG | issue #28-1 托盘"退出"条目排版 | `TrayRender.exe <DshWeb.dll> <out.png>`（见其 README） |
| `realcheck-issue28.ps1` | 真机验证：隔离 `DSH_HOME`/WebView2 数据启动 `DshWeb.exe --ui-probe`，算徽标坐标做**真实鼠标点击**并核验弹窗与进程存活 | issue #28-2 | `pwsh -File sandbox\realcheck-issue28.ps1`，进度写 `realcheck-issue28.log` |
| `issue28-reply.md` / `commit-msg.txt` | 本轮的 issue 回复与提交信息草稿（留档） | — | — |

说明：
- `realcheck-issue28.ps1` 会自建并清空 `sandbox/realrun-issue28/`（隔离 home 与 WebView2 profile，
  体积可达数百 MB，验证后可直接删除）；脚本本体放在 `sandbox/` 根，避免被自身清理逻辑删掉。
- 真机交互链路的**权威回归**在测试工程内（不依赖手工脚本）：
  `tests/DshShell.E2E/UiTestHookE2ETests.cs`（真实 `DshWeb.exe` + 真实鼠标点击徽标）、
  `tests/DshShell.Tests/Regression_Issue28_*.cs`（零 Mock RealOS）。
- 本机输入法相关结论：本机装有第三方 **手心输入法 PalmInput 3.2.9**，是 `ImmSetOpenStatus`
  崩溃的必要环境条件；换机复现请先确认输入法与 Windows 版本。

