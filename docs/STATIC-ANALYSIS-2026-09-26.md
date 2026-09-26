# 静态检查落地记录（2026-09-26）

配套：`.github/SECURITY.md`、`docs/SECURITY-REVIEW-2026-09-26.md`、`Directory.Build.props`、`.editorconfig`。
本文件是**判读表**：每条警告要么真修、要么带证据关闭；`.editorconfig` 里每有一条偏离，
这里必须有一行对应记录，否则视为绕过。

## 1. 工具与配置

| 项 | 值 |
|---|---|
| 源码静态分析 | SDK 内置 **Microsoft.CodeAnalysis.NetAnalyzers**（CA#### 规则集，与编译器 CS#### 是两套规则） |
| 开关 | `Directory.Build.props`：`EnableNETAnalyzers=true`、`AnalysisLevel=latest`、`AnalysisMode=Recommended` |
| 依赖 CVE | `NuGetAudit=true` + `NuGetAuditMode=all`（含传递依赖）→ NU1901..1904；CI 另有 `dotnet list package --vulnerable --include-transitive` |
| 强制 | `TreatWarningsAsErrors=true` + `CodeAnalysisTreatWarningsAsErrors=true` |
| CI 位置 | `.github/workflows/build.yml` → step **"Static analysis (Roslyn CA rules + NuGet advisory audit)"**（push/PR/tag 都跑），产物 `static-analysis`（vuln.log） |
| **刻意不用** | 公开 CodeQL —— 会把未修发现摊成公开清单，与 `SECURITY.md` 的私密报告承诺矛盾（论证见评审文件 §3） |
| 版本面 | net5+ 不得再显式 `PackageReference` 引用 NetAnalyzers 包（NETSDK1141），故以 MSBuild 属性开启 |

## 2. 落地顺序（这个顺序是判据的一部分）

先只开规则、**不**开 warnings-as-errors → 数出警告 → 逐条处置 → 最后才关门。
理由：警告总数事先未知，一上来就 `-warnaserror` 会当场弄碎 CI。

| 阶段 | DshShell | PrereqCheck | FolderPicker | FolderPickerCa | Tests | E2E |
|---|---|---|---|---|---|---|
| A 原状（只有编译器 CS 警告） | 16 | 0 | 1 | 0 | 53 | 17 |
| B +AnalysisMode=Recommended | 70 | 0 | 1 | 5 | 907 | 13 |
| C 测试命名规则按作用域关闭后 | 0 | 0 | 0 | 0 | 79 | 1 |
| D 逐条处置完（关门**前**） | 0 | 0 | 0 | 0 | 46 | 1 |
| E `TreatWarningsAsErrors=true` 打开后 | 0 | 0 | 0 | 0 | **3 错→0** | 0 |

阶段 E 暴露了 23 处**先前就存在**的 xUnit 分析器警告（原状阶段 A 它们以 warning 在，
但我的统计脚本用 `([A-Z]+[0-9]+)` 匹配规则号，看不见小写前缀的 `xUnit####` —— 装置缺陷，已修脚本；
对照组：同一份日志改用正确正则后 46 行 ×2 重复 = 23 处，与 09-21 轮 P3 记的"19 处 xUnit1031"同族，
本日实测为 **20 处 xUnit1031 + 2 处 xUnit1012 + 1 处 xUnit2031**）。

**关门后的最终读数**：6 个工程 `dotnet build -c Release --no-incremental` 全部
`0 Warning(s) / 0 Error(s)`；`dotnet test tests/DshShell.Tests` = **1363 passed / 0 failed**；
`dotnet publish src/DshShell` 干净。

## 3. 逐条处置登记（生产面 src/ + installer/）

| 规则 | 处数 | 处置 | 位置与判读 |
|---|---|---|---|
| CA1305 区域生效的格式 | 23 | **真修** | `DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")` 之类共 7 处进日志时间戳与备份目录名（`.old-yyyyMMdd-HHmmss`、`rollback-bak-…`）：非公历区域（如佛历/伊斯兰历）下 `yyyy` 会产出不同年份，目录名与日志对账因此不可复现 → 补 `CultureInfo.InvariantCulture`。数字侧 16 处（`port.ToString()`、`exitCode?.ToString()`、`int.Parse(...)`、`AppendLine($"…{count}")`）同修 |
| CS8604 / CS8602 / CS8600 可空 | 14 | **真修**（非 `!` 糊） | 评审文件 F1、F6、F7、F8；另 `ShellLogic.PortOpen/PortOpenAsync` 把 `host is null` 的模式判断内联进分支（旧写法先存 bool，流分析证不了非空）；`LegacyUpgradeCleanup` 两处 COM `Activator.CreateInstance` 补显式抛；`SplashForm` 的 `Layout` 属性与 `Control.Layout` 事件撞名（CS0108）→ 更名 `CurrentLayout`（唯一引用点 `UiSelftestProbe.cs:99` 同步） |
| CA1806 Win32 返回值丢弃 | 4 | **真修** | 评审文件 F5 |
| CA1822 可静态化 | 8 | **真修 8** | `WindowChromeController` 两个方法改 static（该类 46 行、无字段，`_chrome` 实例随之删除）；`LauncherApp.TryReadTestDelay`、`DshUpdateManager.KillServiceOnPort/LogPostApplyIdentity`、`VersionInfoDialog.PlaceRow/Trace` 改 static。第 8 条 `DshUpdateManager.TryDowngradeGlobalPackageForRollback` 先被误判为"实现接口故不可 static"，实测它**不在** `IDshUpdateManager` 里（grep 0 命中）→ 也改成 static，调用点 `Program.cs:984` 同步 |
| CA1861 常量数组实参 | 2 | **真修** | `ShellLogic.cs:987/989` 的 `new[]{0,0,0}` → 复用同函数内已全为 0 的 `nums` |
| CA1869 JsonSerializerOptions 每次新建 | 1 | **真修** | `SafeProfileBuilder` → `private static readonly JsonSerializerOptions IndentedOptions` |
| CA1805 显式默认值 | 1 | **真修** | `CustomTitleBar._buildProgressPercent = 0f` → 去初始化 |
| CA1845 Substring | 1 | **真修** | `ShellLogic.Truncate` → `string.Concat(s.AsSpan(0,max), "…")` |
| CA1001 拥有可释放字段 | 2 | **带证据关闭** | `DshUpdateManager`（`_buildCts` 在 661 new、669-670 finally Dispose）与 `MaximizeAcrossVirtualDisplayTests`（`_automation` 在 `DisposeAsync` 73 行 Dispose）。两处都用 `[SuppressMessage]` 行内标注，不扩到全局 |
| CA1068 ct 应为末位参数 | 5 | **带证据降级 silent** | 见 `.editorconfig` 注释：改判据要动 27 个调用点，而这是 WinExe 的内部接口、无外部消费方 |
| CA1050 类型须在命名空间内 | 1 | **带证据关闭（行级 pragma）** | `FolderPickerCa`：MSI 按类型名绑定 4 个 CustomAction（`product.wxs:113-145`），改名要连 wxs 一起动且只能真机装 MSI 验证 |
| CA1016 程序集缺 AssemblyVersion | 1 | **真修** | `FolderPickerCa` 因 `GenerateAssemblyInfo=false`（DTF/SfxCA net20 产物）而没有版本特性 → 文件顶部补 `[assembly: AssemblyVersion("1.0.0.0")]`，理由见下一节 |
| MSB3277 程序集版本冲突 | 1（日志 22 行 ×3 工程） | **真修（根因）** | `Microsoft.Web.WebView2` 的 `build/Common.targets` 对**所有** net5+ 消费者无条件注入 `Microsoft.Web.WebView2.Wpf.dll` 引用；本仓无任何 WPF 代码（全仓 grep `WebView2.Wpf` 0 命中），该程序集把 `WindowsBase 5.0.0.0` 拉进闭包与 SDK 的 10.0 参考冲突。修法＝在 `Directory.Build.props` 里按 AssemblyName 精确摘掉这条 Reference（放根文件是因为包经 buildTransitive 对 Tests/E2E 重放同一引用） |

### CA1016 为什么写死 1.0.0.0
`FolderPickerCa.csproj` 有 `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>`，
所以 SDK 不会生成 `AssemblyVersionAttribute`。补的是
`[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]`（`FolderPickerCa.cs` 顶部）。
写死 1.0.0.0 的理由：这个 CA 程序集经 SfxCA 打进 MSI、按 Binary 流解析，不参与任何按版本绑定，
它的身份就是"这个 MSI 里的那一份"；给它一个会随发布漂移的版本号反而制造出没人读的假精度。

## 4. 测试面处置（tests/）

| 规则 | 处数 | 处置 |
|---|---|---|
| CA1707 标识符含下划线 | 837 | **带证据按作用域关闭**（仅 `tests/**/*.cs`）：837 处全是 xUnit 三段式用例名，重命名会毁掉断言可检索性。生产面该规则仍生效（实测 src/ + installer/ 0 命中） |
| CA1861 常量数组实参 | 23 | **带证据按作用域关闭**（仅 tests）：全部是 `Assert.Equal(new[]{…}, 实际值)` 的期望值字面量，规则前提（反复调用）在用例内不成立 |
| xUnit1031 测试里阻塞等 Task | 20 | **真修 19 + 1 处改写法**：15 处在 `UpdateCheckerTests`（`….Result` → `await`，同时把宿主方法从 `public void` 改成 `public async Task`；`async void` 用例 xUnit 不等待＝假绿灯，所以签名必须一起改）；`RecoveryOutcomes.cs:38`、`SystemUpgradeOutcomeContracts.cs:246/291`、`DshUpdatePipelineRealTests.cs:200` 同修。`BootHealthMonitorTests.cs:524` 原为 `stop.Wait(TimeSpan.FromSeconds(2))`——它测的就是"会不会阻塞"，改成 `await Task.WhenAny(stop, Task.Delay(2000))` + `Assert.True(stop.IsCompleted)`，语义保持且不违反规则 |
| xUnit1012 不可空参数传 null | 2 | **真修**：`IsRetryableNpmError`/`IsNpmNotFoundError` 两个 Theory 的 `string tail` → `string?`（两个被测方法本体都用 `IsNullOrWhiteSpace` 起头，本来就有 null 分支；生产侧 `IsRetryableNpmError` 一并改标）；`[InlineData(null!, false)]` → `[InlineData(null, false)]` |
| xUnit2031 Where 后再 Single | 1 | **真修**：`Assert.Single(urls.Where(…))` → `Assert.Single(urls, …)` |
| CS8619 / CS0219 / CS0414 | 9 / 1 / 3 | **真修**：CS8619 是 fake 探针返回 `Task.FromResult(字面量)` 与注入委托的 `Task<string?>` 不匹配 → 显式 `Task.FromResult<string?>(…)`；`startCalls` 删除（`ProbePort` 根本没有拉起服务的注入点，这个计数器是无源之水）；`F11HookDecisionTests` 两个从未被引用的 `IntPtr` 常量删除（`InlineData` 里是字面量，注释已表意）；`_restartCalls` **不删而是接上**：它本应计数重启，现接到 `StartViaIdentity` 上，并在 `ShuttingDown_AbsorbsSilently`（该用例注释承诺"既不重启也不升级"，而重启侧此前无断言）里断言为 0 —— 结果通过，即该承诺现在有了会红的闸 |

## 5. 正/负对照（证明闸会响，不是空闸）

| 装置 | 负对照（应不报） | 正对照（应报） |
|---|---|---|
| NuGetAudit（含传递） | 本仓 6 工程强制重跑 restore：NU19xx **0** | 仓外夹具引用 `Newtonsoft.Json 12.0.3` → `warning NU1903 … GHSA-5crp-9r3c-p9vr` |
| CI 里的 `dotnet list --vulnerable` | 本仓输出 `has no vulnerable packages given the current sources` | 同夹具输出 `has the following vulnerable packages`，脚本按这句判红 |
| Roslyn CA 关门 | 6 工程 `0 Warning(s)` | 阶段 E 里 `TreatWarningsAsErrors` 立刻把 23 处 xUnit 警告变成构建错误 ⇒ 闸门确实在管 |

## 6. 已知遗留（不假装做完）
1. `_buildCts` 的并发形状：`BuildStagedUpdate` 每次 new 一个 CTS 并覆盖字段，若两次构建并发进来，前一个 CTS 的句柄会被覆盖（`finally` 仍会 Dispose 后一个）。`_buildInProgress` 挡住了"正在跑就不再进"，所以当前不可达；本文件只记录，不顺手改。
2. `static_analysis_common_vulnerabilities`（SUGGESTED）：我们的主张限于"已知 CVE 审计 + CA 安全规则子集"，不含污点/数据流分析。
3. Dependabot 的 alerts / security updates 开关、以及 PVR 开关的当前状态：需仓主在管理页各看一眼（外部读不到，见评审文件 F9/B6）。
4. 静态检查产物是否真在 GitHub 上跑绿：以 `build.yml` 那次 run 的终态为准，见 S15 记录。
