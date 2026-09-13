# sandbox/notify-regression — 更新通知通路回归场景（旧版 dsh × 旧版启动器）

> 遵循仓库核心约束六（单一沙盒根 `sandbox/`）。目的：**在完全不触碰真实环境的前提下**，
> 用"旧版 dsh + 旧版启动器（通知通路已修复的版本）"验证两条更新通知都能到达：
> ① dsh 更新通知（npm latest 0.1.2-rc.1 > 本地旧版）；② 启动器安全更新通知
> （GitHub latest 0.4.5 带 SECURITY 标记 > 当前 0.4.3）。`.gitignore` 已忽略，禁止 `git add -A`。

## 来源与版本

| 项 | 值 |
|---|---|
| 旧版启动器 | `v0.4.3`（GitHub Release 官方资产 zip，SHA256 与 Release 页校验和一致）——**通知通路修复版**：Toast vtable 槽位修复（0.4.1 起从未弹出的根因）+ 更新检查网络多出口/UA/.npmrc 跟随 |
| 旧版 dsh | `@deepseek-ai/dsh@0.1.2-alpha.2`（npm registry；< npm latest 0.1.2-rc.1 → 有更新可报），闭包复制自 `sandbox/dsh-alpha2/global`（npm --prefix 完整安装，222MB） |
| 新版启动器（对照） | 本仓库 `dist/dsh-launcher-windows-0.4.5.zip`（发布构建） |
| 通知目标 | 系统 Toast（`DSH_TEST_INSTALL_MODE=msi` 强制走 MSI 分支的 Toast 通路；AUMID 自注册 HKCU） |

## 目录布局

```
launcher-old/   v0.4.3 便携解压（DshWeb.exe + WebView2Loader.dll + 脚本）
launcher-new/   v0.4.5 便携解压（对照壳）
home/           DSH_HOME（壳数据 + 服务 home 全隔离）
  dsh-launcher/runtimes/0.1.2-alpha.2/node_modules/   ← 旧 dsh 运行时（发现层 SelfContained 布局）
wv2/            DSH_WEBVIEW2_DATA 隔离（防与真实实例互锁，0.3.1 纪律）
staging/        运行日志 / 截图证据
```

## 运行（三次，见 run-notify-test.ps1）

| 运行 | 信号 | 预期通知 |
|---|---|---|
| R1 | 真实 GitHub（0.4.5 SECURITY 已发布）+ 真实 npm | **启动器安全更新 Toast**：`dsh-launcher 安全更新 0.4.5（当前 0.4.3）`（安全更新命中后按设计提前返回，dsh 检查下会话再跑） |
| R2 | `DSH_TEST_UPDATE_SIGNAL=dsh:0.1.2-rc.1`（假信号注入，下游与真实逐字节同通路） | **dsh 更新 Toast**：`dsh 有新版本 检测到 dsh 0.1.2-rc.1（当前 0.1.2-alpha.2）`（本地版本来自发现层真实读取） |
| R3 | 新版壳 0.4.5 + 真实 npm（启动器已是最新 → 无抢占） | **dsh 更新 Toast（真实信号）**：`检测到 dsh 0.1.2-rc.1（当前 0.1.2-alpha.2）` |

## 验证要点

- `home\dsh-launcher\dsh.log`：`update toast shown: ...` 或托盘气泡/标题驻留回退留痕；
  `dsh update check: latest=0.1.2-rc.1 local=0.1.2-alpha.2`。
- 桌面截图：system Toast 实体出现（staging/toast-*.png）。
- 环境隔离：`DSH_SANDBOX=1`（禁机器级副作用）、`DSH_WEB_PORT=3083`（避开宿主 3080/3081）、
  **显式清空宿主注入的 `DSH_WEB_URL`/`DSH_SHELL`/`DSH_SESSION_*`**（不清理会被视为"外部托管"——
  目标被 3080 权威覆盖、发现层 External、隔离失效）、`DSH_TEST_INSTANCE=1`（互斥键隔离）、
  `DSH_TELEMETRY_DISABLED=1`（alpha 线遥测必须关）。
- 清理纪律：只按记录的 PID 杀 DshWeb.exe / node.exe，严禁 `taskkill //IM`。

## 已知行为（设计语义，非缺陷）

- v0.4.3 `ScheduleUpdateCheck`：启动器安全更新命中 → `return`（安全优先），dsh 检查延迟到下会话；
  故同一旧壳会话不会同时弹两个 Toast——这正是发布后真实用户看到的行为。

## 实测结果（2026-09-04 发布后，运行 `./run-notify-test.ps1 -Run R1/R2/R3`）

| 运行 | 信号 | 结果 | 关键证据（dsh.log） |
|---|---|---|---|
| R1 | 真实 GitHub（v0.4.5 SECURITY 已发布）+ 真实 npm | **PASS** | `update toast shown: dsh-launcher 安全更新 / 检测到重要安全更新 0.4.5（当前 0.4.3）。点击查看下载。…`；toast 互操作全步骤 `show ok` |
| R2 | `DSH_TEST_UPDATE_SIGNAL=dsh:0.1.2-rc.1`（下游同通路） | **PASS** | `update toast shown: dsh 有新版本 / 检测到 dsh 0.1.2-rc.1（当前 0.1.2-alpha.2）。点击此处在后台下载更新。`；且 `service start via identity: …runtimes\0.1.2-alpha.2\… bin.js web --host 127.0.0.1 --port 3083` → `poll: ready` → `HEALTHY: __DSH_BOOT__` |
| R3 | 新壳 0.4.5 + 真实 npm | **PASS** | `dsh update check: latest=0.1.2-rc.1 local=0.1.2-alpha.2` → `update toast shown: dsh 有新版本 / 检测到 dsh 0.1.2-rc.1（当前 0.1.2-alpha.2）` |

结论：**两条更新通知通路在沙盒内全部可达**——旧版启动器 v0.4.3（通知通路修复版）收到启动器安全更新通知（真实 GitHub 信号）；dsh 更新通知在旧壳（信号对照，逐字节同一通路）与新壳（真实 npm latest）均收到；沙盒全程隔离（端口 3083、DSH_HOME=本场景 home、WebView2 数据隔离、宿主 3080 服务与进程零接触）。
截图存于 `staging/desktop-R1.png`、`desktop-R2.png`、`desktop-R3.png`；日志 `staging/run-R*.log`。
测试后已按记录 PID 清理全部沙盒进程（3083 无残留监听；宿主 3080 node 进程未触碰）。