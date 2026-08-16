---
name: 报告 Bug
about: 创建一个 Bug 报告来帮助我们改进
title: "[Bug] "
labels: bug
assignees: ""
---

**问题描述**
发生了什么？期望什么、实际怎样。

**复现步骤**（可选）
1. 执行 '...'
2. 点击 '....'

**诊断包（推荐，必填优先级最高）**
在 dsh-launcher 安装目录执行以下命令，把生成的 zip 附到 Issue（已脱敏，不含用户名/密钥）：

```powershell
.\DshWeb.exe --diagnose
```

zip 会生成到"下载"文件夹：`dsh-launcher-diagnose-<时间>.zip`。命令行不方便时，直接附上 `%USERPROFILE%\.dsh\dsh-launcher\dsh.log` 的最后 30 行也可以。

**环境信息**
- Windows 版本：如 Windows 11 22H2
- dsh-launcher 版本：如 0.3.1（安装版见"设置 → 应用"；便携版看 zip 文件名）
- 使用方式：MSI 安装 / 便携版 ZIP
- 是否已全局安装 dsh：是 / 否（`dsh --version` 可查版本）
