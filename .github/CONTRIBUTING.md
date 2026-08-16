# 贡献指南

感谢你愿意为 dsh-launcher 贡献代码！请花两分钟阅读以下约定，让协作更顺畅。

## 开发环境

- Windows 10/11
- .NET SDK 10.0+
- Node.js 18+（运行 dsh 所需，非本仓库构建必需）

## 本地构建

```powershell
# 一键构建发布包（产出 dist/dsh-launcher-windows.zip）
./scripts/build-release.ps1

# 或手动 publish
dotnet publish src/DshShell -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o dist
```

## 提交规范

提交信息使用 [Conventional Commits](https://www.conventionalcommits.org/) 格式：

```
<type>(<scope>): <subject>

feat(shell): 增加系统托盘支持
fix(scripts): 修复开机自启脚本在中文路径下失败的问题
docs(readme): 补充版本兼容性说明
```

常用 type：`feat` / `fix` / `docs` / `refactor` / `ci` / `chore` / `test`。

## 分支与 PR 流程

1. 从 `master` 创建功能分支：`git checkout -b feat/my-change`
2. 完成修改并本地验证（见上"本地构建"）
3. 更新相关文档（README / CHANGELOG）
4. 提交并推送，然后创建 Pull Request
5. 在 PR 中说明变更动机和验证方式（模板已内置检查清单）

## 代码约定

- 壳应用为 C#（WinForms + WebView2），风格遵循现有代码（file-scoped namespace、隐式 using）
- 脚本（VBS / CMD / PowerShell）**不要硬编码用户路径**，一律使用 `%USERPROFILE%`、`%~dp0` 等环境变量或相对路径
- 涉及端口 / 启动参数变更时，必须同步修改 `start-dsh.vbs`、`dsh-web.cmd`、`Program.cs` 三处并更新 README

## 行为准则

请遵守 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。违反者将被移除贡献资格。
