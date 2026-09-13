# update-lab — dsh 手动更新沙盒实验室

在完全隔离的环境里测试 dsh-launcher 的**手动更新全流程**，绝不触碰宿主机的 dsh 服务与启动器。

## 场景（镜像生产现实）

- 已安装基线：`@deepseek-ai/dsh 0.1.0-rc.6`（假包，页面会显示版本号）
- 本地假 registry 的 latest：`0.1.1-rc.2`
- 更新走真实代码路径：托盘 Toast → 确认 → `npm pack`（本地 registry）→ pnpm hoisted 构建 → `pending-update.json` → 重启原子应用

## 一键操作（双击即可）

| 文件 | 作用 |
|---|---|
| `start-rc6-and-launch.cmd` | **一键切回 rc6 并拉起**：停旧实例 → 秒回 rc6（纯本地文件恢复，零下载）→ 起本地 registry → 拉起启动器 |
| `launch-resume.cmd` | 不重置直接拉起：有 pending 时启动即自动安装新版本 |
| `reset-to-rc6.cmd` | 停止并秒回 rc6（不拉起） |
| `status.cmd` | 查看进程/端口/运行时/pending/stash 状态 |

## 典型测试循环

1. 双击 `start-rc6-and-launch.cmd` → 沙盒窗口显示 `Version: 0.1.0-rc.6`，弹出"有新版本 0.1.1-rc.2"Toast
2. 点击 Toast → 确认 → 进度条（本地秒级完成）
3. 关闭沙盒窗口 → 双击 `launch-resume.cmd` → 启动即自动应用 → 页面显示 `Version: 0.1.1-rc.2`
4. 回到第 1 步即可无限重复；每轮"切回 rc6"都是本地文件恢复，**不需要重新下载**

## 为什么不会影响宿主（安全模型）

- **专用应用副本**：沙盒运行 `app\DshWeb.exe`（setup 从 dist\dev 镜像），与宿主启动器的 exe 物理分离
- **杀进程三重守卫**：只杀 pid 文件记录的 PID（校验是 lab 副本）/ WebView2 子进程带 `--user-data-dir=<lab>` 标记的实例 / 服务与 registry 还要求可执行文件位于 sandbox 树内。无任何按路径清扫
- **环境隔离**：`DSH_SANDBOX=1`（禁自启/ProgramData 清理）、`DSH_HOME=lab\home`、服务端口 3999≠宿主 3080、`DSH_WEBVIEW2_DATA` 独立、npm 缓存独立；继承的 `DSH_*` 变量全部先清除（防外部注入把目标劫持到宿主 3080）
- **网络隔离**：`DSH_NPM_REGISTRY/MIRROR=http://127.0.0.1:4873`（本机假 registry，仅绑定 127.0.0.1）；GitHub 检查失败静默不影响
- 每次操作前显式列出检测到的宿主启动器并承诺不碰

## 已验证的事实（2026-08-22）

- rc6 启动 → Toast → 点击下载构建 → 重启自动应用 rc.2 → 页面显示 0.1.1-rc.2 ✅
- reset 后 rc6 秒回（robocopy 本地快照恢复）✅
- 宿主启动器运行时执行 lab stop/reset/start，宿主进程零误伤（实测回归）✅

## 目录结构

```
update-lab/
├── app\            沙盒专用启动器副本（勿手删，setup 会重建）
├── home\           DSH_HOME（runtimes/staging/pending/日志都在这）
├── snapshot\       rc6 纯净快照（秒回用）
├── stash\          未应用的 staged 构建暂存（可 -ReuseStash 免下载重放应用阶段）
├── registry\       config.json（latest 可改）+ tarballs
├── logs\           registry / npm-pack / 实验日志
└── pid\            进程 PID 记录 + run-launcher-env.cmd（生成的环境脚本）
```

改"最新版本"：编辑 `registry\config.json` 的 `latest` 后重启 registry（`lab.ps1 stop && start`），或直接改 `lab.ps1` 里 `$Target` 再跑 setup。
