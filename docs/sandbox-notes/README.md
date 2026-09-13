# 沙盒场景文档归档（sandbox-notes）

> 2026-09-14：项目停更前清理，`E:\dsh-launcher\sandbox\`、`E:\dsh-lab\`、
> `E:\dsh-compat-sandbox\` 三个本地沙盒/实验室目录（约 5.3 GB）已删除。
> 删除前把其中**不可从仓库重建的文档与脚本**原样归档到本目录；
> 治理规范本身在 [`00-ARCHITECTURE-GUARDRAILS-MANDATORY.md`](../00-ARCHITECTURE-GUARDRAILS-MANDATORY.md)
> 核心约束六，%TEMP% 瞬态沙盒的构建代码在 `scripts/negative-test.ps1`、
> `scripts/update-drill.ps1` 与 `tests/DshShell.Tests/Sandbox/DshSandbox.cs`，均随仓库保留。

## 归档内容

| 文件 | 来源 | 说明 |
|---|---|---|
| `SANDBOX-ROOT-README.md` | `sandbox/README.md` | 单一沙盒根总纲（含 `DSH_TELEMETRY_DISABLED=1` 关遥测技巧——铁律文档未收录） |
| `issue24-regression.md` | `sandbox/issue24-regression/` | issue #24（E2001 硬编码 %APPDATA%\npm）+ E2003 复现场景说明 |
| `issue25-wpnapps.md` | `sandbox/issue25-wpnapps/` | issue #25（Win10 wpnapps.dll 崩溃）复现与修复验证场景 |
| `issue26.md` | `sandbox/issue26/` | issue #26（就绪前服务退出盲等）复现与修复验证场景 |
| `notify-regression.md` | `sandbox/notify-regression/` | 旧版 dsh × 旧版启动器更新通知通路回归场景 |
| `version-info-demo.md` | `sandbox/version-info-demo/` | 标题栏 dsh 版本徽标功能演示场景 |
| `dsh-alpha2.txt` | `sandbox/dsh-alpha2/` | dsh 0.1.2-alpha.2 沙盒安装记录（结论已并入 dsh-plugins 契约账本） |
| `DSH-LAB-README.md` | `E:\dsh-lab\README.md` | 沙盒/测试环境统一根目录（dsh-lab）的结构说明 |
| `update-lab-scripts/` | `E:\dsh-lab\sandbox\update-lab\` | 手动更新全流程实验室：README（完整安全模型）+ `lab.ps1` + 4 个一键 .cmd。按文档可整体重建（假 registry、专用 app 副本、三重杀进程守卫） |
| `compat-sandbox-scripts/` | `E:\dsh-compat-sandbox\` | 一次性 dsh 安装迁移/实验脚本（fix-closure / flatten / hash-profiles）；契约调查结论在 `E:\dsh-plugins\docs\compat\` 账本 |

## 重建指引

- 常规回归/负路径测试：直接跑 `scripts/test.ps1`（脚本内 %TEMP% 隔离，零依赖）。
- dsh 版本兼容调查：按 `DSH-COMPAT-CHANNEL.md` 走账本流程，沙盒布局照
  `SANDBOX-ROOT-README.md` 的 `global/` + `home/` + `staging/` 三段式。
- 手动更新全流程演练：照 `update-lab-scripts/README.md` 重建（假包源来自
  `npm pack` 本地包，快照恢复用 `robocopy /MIR`）。
