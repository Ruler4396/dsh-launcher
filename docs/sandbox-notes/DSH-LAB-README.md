# dsh-lab — DSH 沙盒与测试环境统一根目录

> 2026-08-24 目录结构整理：E 盘根下分散的沙盒/测试目录统一收拢到这里。
> **只放沙盒和测试环境；正式代码在 `E:\dsh-launcher`，插件在 `E:\dsh-plugins`。**

## 布局

```
E:\dsh-lab\
├── sandbox\                      ← 复用：活跃沙盒（原 E:\dsh-launcher\sandbox，整体搬入）
│   ├── update-lab\               ←   手动更新全流程实验室（一键 .cmd 入口，详见其 README.md）
│   ├── env\node\                 ←   实验室专用 Node 运行时
│   ├── fake-registry\tarballs\   ←   本地假 registry 的假包源
│   ├── npm-prefix\
│   └── real-home\
└── archive\
    └── 2026-08-24\               ← 归档：过时测试残留（未删除，仅移入）
        ├── dsh-update-test\             原 E:\dsh-update-test（约 420MB npm/pnpm 离线实验，
        │                                2026-08-20 一次性产物，已被 update-lab 取代）
        ├── dsh-safe-mode-sandbox\       原 E:\dsh-safe-mode-sandbox（安全模式测试残留）
        ├── dsh-launcher-test-staging\   原 E:\dsh-launcher-test-staging（npm 调试残留：日志 + prefetch_temp）
        └── publish-test\                原 E:\dsh-launcher\.publish-test（dotnet publish 输出快照）
```

## 归档说明

- 归档目录均为**原样移动**（同卷 rename），内容零改动。
- 全部归档项在仓库代码、脚本、文档中**均无引用**（已全量 grep 验证）。
- `sandbox/` 与 `.publish-test/` 本就在 `E:\dsh-launcher\.gitignore` 中，仓库 git 状态不受影响。
- 若确认不再需要，可整批删除 `archive\2026-08-24\` 释放约 422MB。

## update-lab 快速入口（位置无关，双击即用）

| 文件 | 作用 |
|---|---|
| `sandbox\update-lab\start-rc6-and-launch.cmd` | 切回 rc6 并拉起沙盒启动器 |
| `sandbox\update-lab\launch-resume.cmd` | 直接拉起（有 pending 则自动应用更新） |
| `sandbox\update-lab\status.cmd` | 查看进程/端口/运行时/pending/stash 状态 |

脚本全部以 `$PSScriptRoot` / `%~dp0` 自定位，宿主仓库根已在 `lab.ps1`
中显式固定为 `E:\dsh-launcher`（仅作引用比对，从不触碰）。
