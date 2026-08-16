---
name: 报告 Bug
about: 创建一个 Bug 报告来帮助我们改进
title: "[Bug] "
labels: bug
assignees: ""
---

**描述问题**
清晰简洁地描述这个 Bug 是什么。

**复现步骤**
1. 执行 '...'
2. 点击 '....'
3. 看到错误

**预期行为**
清晰简洁地描述你期望发生什么。

**实际行为**
实际发生了什么？如有报错信息请完整贴出。

**截图 / 日志**
如有，附上截图。日志在 `%USERPROFILE%\.dsh\dsh-launcher\dsh.log`（统一日志）；推荐一键导出诊断包（含脱敏日志/环境/错误码）：

```powershell
# 在 dsh-launcher 安装目录执行：
.\DshWeb.exe --diagnose
```

会生成 `dsh-launcher-diagnose-<时间>.zip` 到"下载"文件夹，直接附到 Issue 即可（已脱敏，不含用户名/密钥）。

**环境信息**
- Windows 版本：如 Windows 11 22H2
- dsh-launcher 版本：（如 0.3.1，安装版可在"设置 → 应用"查看）
- dsh 版本：（运行 `dsh --version`，或用 `--diagnose` 打包）
- 使用方式：MSI 安装 / 便携版 ZIP
- 是否已全局安装 dsh：是 / 否

**其他上下文**
任何其他有助于定位问题的信息。
