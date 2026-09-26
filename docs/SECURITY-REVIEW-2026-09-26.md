# dsh-launcher 安全评审（2026-09-26）

> **评审对象**：`Ruler4396/dsh-launcher` master @ `3f3e673f3abbd00a5ac53ffa27afd432f00be22f`
> （2026-09-26 08:40 +0800，v0.5.1 之后的 6 个提交），含本回合落地的静态检查改动。
> **方法**：以 `.github/SECURITY.md` 声明的威胁模型为边界，逐条复核 2026-09-21 全量质量审查
> 里安全相关的条目**在当前代码上是否仍然成立**，并叠加本日静态检查（Roslyn CA 规则集 +
> NuGet advisory 审计）新暴露的发现。
> **证据强度**（与 09-21 轮同一套口径，"声称强度必须匹配测量强度"）：
> ✅=本回合读原文/跑命令实测；◇=转述 `docs/reviews/2026-09-21-quality-review.md`，本回合未复现其运行时后果；
> ○=未判定（需要真机 / 需要仓主在管理页确认）。

---

## 1. 信任边界

| # | 边界 | 跨界输入 | 现在的守卫 | 证据 |
|---|---|---|---|---|
| B1 | 用户 → 壳进程（文件/命令行） | `settings.yaml`、命令行开关、`DSH_*` 环境变量 | `--host` 默认 `127.0.0.1`；环境变量入口有白名单台账 | ✅ SECURITY.md「安全设计说明」；◇ 09-21 轮 P3 记 `DSH_TEST_SPLASH_DELAY_MS` 未入台账 17 名单 |
| B2 | 壳 → 本机 HTTP 服务 | `127.0.0.1:3080` 的响应与弹窗请求 | `WebViewPolicy.ClassifyPopup` 判 host | ✅ 见发现 F3（只判 host 不判端口） |
| B3 | 壳 → 网络（下载 Node.js / dsh 包 / 版本检查） | 镜像 zip、`SHASUMS256.txt`、GitHub Releases / npm registry JSON | 校验和优先官方源、`AtomicWrite`、失败码 E1003/E1004 | ✅ 见 F1、F2；`RuntimeResolver.VerifySha256Async` 本回合读到实现 |
| B4 | 低权限用户 → 安装器（MSI 自定义动作） | `C:\ProgramData\dsh-launcher\picked.txt`（Users 可写）里的"路径 + 一次性令牌" | 令牌须形如 32 位十六进制；路径须本地绝对路径；拒绝系统目录与 UNC | ✅ `installer/FolderPickerCa/FolderPickerCa.cs:7-16` 声明的防线 + 本回合修掉的 F4 |
| B5 | 依赖 → 供应链 | NuGet 包、GitHub Actions 版本 | Action 全 SHA pin；**本日新增** NuGetAudit（含传递依赖）+ Dependabot 配置 | ✅ `build.yml` 顶注、`Directory.Build.props`、`.github/dependabot.yml` |
| B6 | 报告者 → 维护者 | 漏洞报告 | `SECURITY.md` 指向 Private vulnerability reporting | ○ PVR 开关的外部可见性无法判定：`gh api repos/.../security_and_analysis` 以 owner 身份仍返回 **404**，只有管理页能判读 |

## 2. 结论：发现清单

### F1 · 校验和回退源会拼出畸形 URL（本回合修）
`RuntimeResolver.VerifySha256Async` 的候选清单第二条是 `baseUrl + "/SHASUMS256.txt"`，而
调用方 `baseUrl` 可为 null（同函数上一行就写着 `if (baseUrl is not null) RecordLastMirror(baseUrl);`）。
null 拼接得到相对串 `"/SHASUMS256.txt"`，`GetStringAsync` 抛出的格式异常又被内层
`catch { /* 尝试下一个源 */ }` 吞掉 —— **结果是"官方源失败时还有一条回退"这个设计在
baseUrl 缺失时静默不存在**，只表现为 E1004。
✅ 编译器 CS8604 报出（`src/DshShell/RuntimeResolver.cs:116`）。
✅ 缓解：参数改标 `string?`，null 时不再把该候选放进清单（本回合，同一改动里）。

### F2 · 供应链事故的归因仍然偏粗（09-21 轮 N10 的**残留部分**）
`RuntimeResolver.cs:275-283` ✅：网络/文件锁/解析异常已被区分记录
（`checksum verification errored (NOT necessarily a mismatch)`，注释点名 N10），
但**返回值语义没变**：任何异常一律 `false`，调用方据此报 **E1004「校验和不匹配，可能源被篡改」**。
即：日志层修好了，用户看到的那句结论仍会把网络故障说成完整性事故。
◇ 影响：误导用户去怀疑镜像，掩盖真实故障面。**未修**，理由见 §4。

### F3 · 可信弹窗判定不看端口（09-21 轮 N14，本回合复核仍在）
`ShellLogic.cs:166` ✅：`uri.Host is ("127.0.0.1" or "localhost") ? Internal : External`。
本机任意端口上起的服务都能拿到"壳内可信窗"的待遇 → UI 欺骗面（伪造壳自己的界面/文案）。
**未修**，属于 B2 边界的设计决策（改判据要同时定义"合法端口从哪来"），不是能顺手加一行的小事。

### F4 · 安装器提权守卫用了区域生效的字符串比较（本回合修）
`installer/FolderPickerCa/FolderPickerCa.cs` 的 `IsSafeInstallPath` 是 B4 边界的唯一防线，
其中三处判定原先依赖**当前用户的区域设置**：
- `root.EndsWith("\\")`（须为盘符根/UNC 根）
- `full.StartsWith("\\\\")`（**拒绝 UNC**，即"本机安装目标是本地盘"这条）
- `string.Equals(opt, "1")`（一次性标志比对）

区域敏感比较会做全半角与 expansion 归一，而路径前缀守卫要的是字节级成立与否。
同文件其余 5 处比较（`:70`、`:160-161`、`:242`、`:338`）**本来就写了 Ordinal**（`git show HEAD` 里
`StringComparison` 恰好 5 处）—— 这三处是同一函数内的不一致，不是设计选择。
✅ CA1310/CA1309 报出；✅ 缓解：三处补 `StringComparison.Ordinal`（本回合）。
注：这是把守卫**收紧**，不是放宽。

### F5 · Win32 返回值被丢弃，导致"看起来没生效"无从判断（本回合修）
四处 P/Invoke 的 HRESULT/返回值被扔掉：`DwmSetWindowAttribute`（阴影）、`DwmFlush`、
`DwmGetWindowAttribute`、`ReleaseDC`。其中两处是真问题：
- `DwmGetWindowAttribute` 失败时 `actual` 是未初始化值，旧代码把它当事实打进 Trace
  → 诊断信息会说谎（✅ CA1806，`Program.cs:2829`）。
- `ReleaseDC` 返回 0 = 屏幕 DC 未归还，累积会拖垮整机绘制，旧代码零留痕（✅ `TrayMenuForm.cs:309`）。

✅ 缓解：失败改为写一行带 hr 码的留痕（本回合）。

### F6 · 后台无主窗时，更新失败模态被静默吞掉（本回合修）
`Program.PostStagedModal(Form form, …)` 用 `form.BeginInvoke(...)` 弹 E4001；
调用链上 `form` 来自 `GetMainFormForDialog()`（该函数契约就是"可能返回 null"，
其调用点自己写着 `form?.Activate()`）。form 为 null 时抛 `NullReferenceException`
→ **恰好被同一行的 `catch` 吞成一条 Warn**，于是"构建失败"这件事在用户面前完全消失。
✅ CS8604/CA 链报出；✅ 缓解：null 时退化为无主窗的应用级模态（本回合，`Program.cs`）。

### F7 · 日志增量扫描判空与使用不同变量（本回合修）
`BootHealthMonitor.cs:296` ✅：`var body = text ?? string.Empty;` 之后用 `body.Length > 0` 判空，
却仍对 `text` 调 `Split` —— 可空解引用。CS8602 报出，改成遍历 `body`。
安全意义：这条是 boot 错误签名扫描链（插件致命/E2007 归因）的读循环，异常即监督失效。

### F8 · 服务输出证据流里的 null 语义（本回合修）
`ServiceManager.cs:501-502` ✅：`OutputDataReceived += (_, e) => Append(e.Data)`，
而 `e.Data == null` 表示**流关闭**，不是一行日志。旧写法把它当行送进
`Append(string line)`（形参不可空）。改为显式跳过——保留"丢了行要留痕"的原逻辑
（`Append` 内部对真行仍走 F24 重试与 dropped-line Warn）。

### F9 · 依赖漏洞监测（本日新建，状态：闸门在跑，告警面待开）
✅ 本回合实测：6 个工程在 `NuGetAudit=true` + `NuGetAuditMode=all`（含传递依赖）下强制重跑
restore，**NU1901..1904 命中 0**；正对照（仓外夹具引用 `Newtonsoft.Json 12.0.3`）报出
`NU1903 ... known high severity vulnerability, GHSA-5crp-9r3c-p9vr` ⇒ 这条闸不是空闸。
✅ 配合 `TreatWarningsAsErrors=true`：以后任何**已知 CVE 的依赖版本**会让 CI 直接红。
○ Dependabot 的 **alerts / security updates 是仓库 Settings 开关**，配置文件替代不了；
  且 `security_and_analysis` 接口对本仓返回 404，外部无法判读 ⇒ 需仓主在管理页确认一次。

### F10 · 未纳入本次评审面的东西
- 运行时行为验证（真机安装/卸载、SmartScreen 未签名提示、提权路径实做验证）：○ 未做。
- 代码签名：本轮明确**不做**（证书要年费，与现金约束冲突），`docs/DETAILS.md` 已自述未签名。
- 09-21 轮的 P1 行为缺陷（N1 单实例句柄、N2 崩溃钩子注册时序、N3 状态机滞留等）：
  它们是可靠性缺陷而非安全缺陷，本文件不重复判定，只在 §4 标出哪些仍开放。

## 3. 与"公开 CodeQL"的取舍（记录决策，免得下次重新论证）
不开。公开 CodeQL 会在仓库 Security 页生成**任何人都能点开的未修发现清单**，
与 `SECURITY.md`「不要在公开 Issue 披露安全漏洞 / 请走私密报告」的承诺方向相反。
本仓的 `static_analysis` 由 **Roslyn CA 规则集（AnalysisMode=Recommended）+ NuGet advisory 审计**
承担，判读表见 `docs/STATIC-ANALYSIS-2026-09-26.md`。
代价要写清：CodeQL 的规则面比 Roslyn 宽（尤其污点/数据流类），所以
`static_analysis_common_vulnerabilities`（SUGGESTED，不阻塞 passing）我们只敢主张"已知 CVE + CA 安全规则子集"，
不敢主张"污点分析"。

## 4. 缓解与开放项

| 项 | 状态 | 责任人/下一步 |
|---|---|---|
| F1 null 拼 URL | **已修**（本回合） | — |
| F2 E1004 归因粒度 | **开放**（刻意不修） | 需要改的是"错误码语义 + 对应用例 + 用户文案"三件套，属更新链所有者；本文件把它记成带位置待办而不是已办 |
| F3 弹窗不判端口 | **开放** | 需要先定义合法端口来源（`Target.Port` 可达性），是设计决策 |
| F4 区域敏感路径守卫 | **已修**（本回合，3 处 Ordinal） | — |
| F5 Win32 返回值 | **已修**（本回合，4 处留痕） | 真机是否真有失败 ○ |
| F6 无主窗吞模态 | **已修**（本回合） | — |
| F7 / F8 可空与 null 语义 | **已修**（本回合） | — |
| F9 CVE 闸门 | **已落地**（构建期） | 仓主开 Dependabot alerts/security updates 开关 |
| 09-21 轮 N12（`AppEnvironment.cs:204` 裸 `File.WriteAllText` 写 settings.json，同文件 `:176` 已用 AtomicWrite） | **仍在**（本回合 grep 复核 ✅） | 违反 docs/00 核心约束二.2，属更新/迁移链 |
| 09-21 轮 N13/N17/N18/N25 | 未在本回合复核 | 不在 B1~B5 的判定面上，保持原文件的开放状态 |

## 5. 复现本评审的命令
```
git rev-parse HEAD                                   # 3f3e673…（改动前基线见 §评审对象）
dotnet build src/DshShell/DshShell.csproj -c Release -warnaserror --nologo
dotnet restore <每个工程> /p:NuGetAudit=true /p:NuGetAuditMode=all
dotnet list <每个工程> package --vulnerable --include-transitive
```
逐条警告的处置登记在 `docs/STATIC-ANALYSIS-2026-09-26.md`。
