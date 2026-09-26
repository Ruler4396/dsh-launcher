# 安全策略

## 支持的版本

| 版本 | 支持状态 |
| --- | --- |
| 最新 Release | ✅ 支持 |
| 历史版本 | ❌ 仅报告，不修复 |

## 报告漏洞

请**不要**在公开的 Issue 中披露安全漏洞。

请通过 GitHub 的 [Private vulnerability reporting](https://github.com/Ruler4396/dsh-launcher/security/advisories) 功能提交，或在 Issue 中注明 `security` 标签并仅在私下讨论。

报告中请包含：

- 漏洞类型与影响范围
- 复现步骤（尽量精简）
- 受影响的版本
- 如可行，附上修复建议

## 安全设计说明

- 本项目是本地工具，默认仅监听 `127.0.0.1`，不对外暴露端口
- 壳应用仅加载本地 `127.0.0.1:3080` 页面，未启用远程内容访问
- 脚本不包含任何凭据，日志仅记录服务输出
- 若你修改 `--host` 对外提供服务，请自行评估暴露面（dsh 官方对此也有相应安全要求）

### 已知边界（平台固有，不视作漏洞）

- **PATH 解析**：`npm.cmd`（dsh 更新）、`where dsh`（服务探测）、`npx -y`（首次拉取）都按
  `%PATH%` 顺序解析。若用户可写目录排在系统目录之前，同权限攻击者可劫持执行链。这是
  Windows 的通用权限模型（同权限下无法互相提权），建议用户不要将不可信目录加入 PATH。
- **HKCU 开机自启的归属**：自启项写入**当前安装用户**的 HKCU。若用另一个管理员账户通过
  UAC 执行安装/卸载，自启会写入该管理员的 HKCU，原安装用户不会获得开机自启。这是
  per-user 自启的固有语义；如需对特定账户生效，请在该账户下运行安装向导并勾选自启。

## 机械检查与评审记录

- 带日期的安全评审（评审对象/信任边界/结论/缓解四段）：`docs/SECURITY-REVIEW-2026-09-26.md`。
- 静态检查跑什么、每条警告怎么处置：**不开公开 CodeQL**（公开告警清单与上面的私密报告承诺相矛盾），
  改用 SDK 内置 Roslyn 分析器（`AnalysisMode=Recommended`）+ NuGet advisory 审计（含传递依赖），
  两者都由 `Directory.Build.props` 打开并经 `TreatWarningsAsErrors` 变成 CI 红灯；
  判读表见 `docs/STATIC-ANALYSIS-2026-09-26.md`，CI 步骤在 `.github/workflows/build.yml`。
- 依赖版本更新由 `.github/dependabot.yml` 提 PR；**Dependabot alerts / security updates
  是仓库 Settings 里的开关**，配置文件替代不了它们。

