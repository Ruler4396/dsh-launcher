## v0.5.1

## [0.5.1] - 2026-09-20

**常规维护更新，不是安全公告**：本次不发应用内推送（启动器只对正文里带那个全大写英文标记的Release 弹提示），装上与否由你自己决定；但如果你用 **fnm / nvm / volta / scoop / chocolatey**管 Node，v0.5.0 那个 MSI 会对着你机器上真实可用的 node 报"缺少 Node.js 18+"并拒绝安装——这一版就是修它。

### 修复

- **MSI 前置检查对着 fnm/nvm/volta 装的 node 说"你没有 Node.js"（v0.5.0 发布当晚本机实测）**：用户在有 node v24.21.0 的机器上被 `dsh-launcher 安装 - 缺少运行环境` 弹窗拦下。旧`PrereqCheck.DetectNode` 只有两条判据——进程 PATH 里找 `node.exe`、以及`HKLM\SOFTWARE\Node.js\InstallPath` 存在即放行。版本管理器装的 node **两条都不满足**：fnm 是**每个 shell 会话**用 `fnm env` 注入一个 `fnm_multishells\<pid>_<ts>` 软链目录，持久 PATH（注册表里那份，也正是 msiexec 看到的那份）里一个 node.exe 都没有；fnm 也不写官方安装器的注册表键。实测本机：旧算法在同一世界下判 `hasNode=False`（与截图一致），而`%APPDATA%\fnm\aliases\default\node.exe` 跑起来就是 v24.21.0。修复：候选路径枚举下沉为纯函数 `NodeLocator.EnumerateCandidates`（不碰文件系统，可逐条核对），扫三份 PATH（进程 / 机器 / 用户）+ fnm（`FNM_DIR`、`aliases\{default,lts-latest,lts}`、`node-versions\<ver>\installation`）+ nvm-windows（`NVM_SYMLINK`）+ volta + scoop + chocolatey。**顺带堵掉反向缺陷**：注册表那条兜底过去只判 `File.Exists` 不判版本——本机残留的`InstallPath=D:\node\`（node 24.13.1 时代留下，目录已删）就是活例子，若那里还躺着个 node 12旧实现会照样放行；现在所有候选一律实跑 `node --version` 并要求主版本 ≥ 18。验证：新增 `PrereqCheck.exe --selftest-node <结果文件>`（WinExe 无控制台，结论落文件），本机三例实测——持久 PATH 世界下 `found=1 node=…\fnm\aliases\default\node.exe v24.21.0` 放行；把版本管理器落点全指向空目录 + PATH 清空 → `found=0` 仍然拦得住（不是"改成永远放行"）；旧算法对照实测 `hasNode=False`，证明这条修复针对的是真实假阴性。
- **同一盲区也在便携 ZIP 的 `scripts/check-prereq.cmd` 里**（它只提示、不拦安装，但会给出同样的错误结论）：该脚本用 `node --version` 走 PATH，双击运行时拿到的是 Explorer 的持久 PATH，fnm 用户照样看到 `[MISSING] Node.js 18+`。实测改前 `exit=1 / MISSING`，改后`[OK] Node.js 18+ - found via %APPDATA%\fnm\aliases\default\node.exe / exit=0`；反例（把`FNM_DIR`/`APPDATA`/`LOCALAPPDATA`/`USERPROFILE`/`NVM_SYMLINK`/`VOLTA_HOME` 全指向空目录 +PATH 清空）仍是 `MISSING / exit=1`，不是改成永远放行。

## 校验和 (SHA256)
```text
e7a4ee83a3ebf68ef11060e057277788181f6400c696047e4237ef43adb17453  dsh-launcher-windows-0.5.1.zip
7003a35174d3bee2c27c4e5b11bff1073a69c95067cbf9b0bf6c8b14bd34a134  dsh-launcher-0.5.1.msi
```
---

## 安装与卸载 / Install & Uninstall

**MSI 安装包（推荐新手）**：双击安装，向导里可勾选是否开机自启；自动创建桌面与开始菜单快捷方式（含"卸载 dsh-launcher"）。卸载：设置 → 应用 → dsh-launcher → 卸载。

**便携版 ZIP**：解压即用，双击 `DshWeb.exe`；删文件夹即卸载（自启/快捷方式用 `uninstall-autostart.cmd` 清理）。ZIP 为框架依赖发布，**解压后建议先运行同目录 `check-prereq.cmd` 确认已安装 .NET Desktop Runtime 10 与 Node.js 18+**（MSI 安装包自带前置检查，无需手动）。

> MSI 与 ZIP 内容完全相同；区别只在安装方式：MSI 有标准安装/卸载流程，适合新手；ZIP 免安装，适合便携党。
> The MSI and ZIP contain the same files; the MSI adds a standard install/uninstall flow for new users, the ZIP is portable and install-free.
