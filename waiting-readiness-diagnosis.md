# dsh-launcher 0.4.0「卡在等待服务就绪 → 180s 超时退出」诊断报告

> 日期：2026-08-19 | 复现环境：Windows 11 + .NET 10 single-file 发布包（ZIP 免安装版）
> 现象：双击 `DshWeb.exe` → Splash 窗显示「正在等待 dsh 服务就绪…」→ 约 3 分钟后窗口消失（`poll: timeout after 180s`，错误码 E2002）。

---

## 一、环境与运行产物（已核实）

| 项 | 值 |
|---|---|
| 用户实际运行的 exe | `E:\Chrome Download\dsh-launcher-windows-0.4.0\DshWeb.exe` |
| exe 大小/时间戳 | 1,238,091 字节 / 2026-08-19 19:04:42 |
| 同目录 `start-dsh.vbs` | 4,355 字节 / 2026-08-19 18:51:10（**含 npm shim 探测分支的新版**） |
| 进程 PID | 34352，启动于 2026-08-19 19:15:21 |
| 窗口内容（UIA 读取） | Label/ProgressBar 文本 =「正在等待 dsh 服务就绪…」，存在「取消」按钮 |
| 进程子进程 | **无**（无 wscript / cmd / node / npx 子进程） |
| 端口 3080 | **无监听** |
| `DSH_HOME` 环境变量 | 未设置（日志路径 = `%USERPROFILE%\.dsh\dsh-launcher\dsh.log`） |
| 构建方式 | `dotnet publish -r win-x64 --self-contained false -p:PublishSingleFile=true` |

---

## 二、日志关键证据（`C:\Users\enmusubi4\.dsh\dsh-launcher\dsh.log`）

### 2.1 本次失败（PID 34352，19:15 启动）——日志极不完整

19:15:21 进程启动后，dsh.log **没有任何**以下本应存在的记录：

- `feature flag: DSH_USE_NEW_LIFECYCLE=...`（Main 第 302-306 行，启动即写）
- `start target=... external=False`
- `first instance`
- `lifecycle: CheckingInstance / ResolvingRuntime / StartingService`
- `service start requested via start-dsh.vbs`
- `[start-dsh] using port 3080`（vbs 侧输出）

**直到 19:19:13 才出现三条**：

```
19:19:13.831 INFO  pid 34352  "poll: timeout after 180s"
19:19:13.833 INFO  pid 34352  "lifecycle: ShuttingDown"
19:19:13.834 ERROR pid 34352  E2002 "service readiness failed: timeout"
19:19:14.291 ERROR pid 34352  E2002 "dsh 服务未能就绪。启动超时：可能是首次下载 dsh 组件较慢（可稍后重试），也可能是网络/代理问题。日志尾部：…完整日志：C:\Users\enmusubi4\.dsh\dsh-launcher\dsh.log"
```

**结论：34352 的全部早期日志静默丢失**（`Logger.Write` 写失败静默 catch），只有 kill 残留进程后（见 2.3）才写入了最后几条。

### 2.2 更早的两次成功/失败对照（同一天）

```
17:45:31 PID 14680 first instance → CheckingInstance → ResolvingRuntime → StartingService
17:45:37 "applying staged dsh update to 0.1.0-rc.7"   ← 耗时 IO（约 100 秒）
17:47:28 "staged dsh update applied: 0.1.0-rc.7"
17:47:28 "service start requested via start-dsh.vbs"
17:47:28 [start-dsh] using port 3080 / [start-dsh] using global dsh / dsh web: http://127.0.0.1:3080  ← 成功

18:17:15 PID 28652 first instance
18:17:20 "applying staged dsh update to 0.1.0-rc.7"    ← 再次尝试（约 120 秒）
18:19:20 WARN E4002 "staged dsh update apply failed; continuing with current version"
18:19:20 CheckingInstance → ResolvingRuntime → StartingService
18:19:24 "service start requested via start-dsh.vbs"
18:19:24 [start-dsh] using port 3080 / [start-dsh] dsh not on PATH - npx via https://registry.npmmirror.com
          dsh web: http://127.0.0.1:3080               ← 服务实际打印了 URL，但后续无 ready 日志
18:40:52 PID 31588（用户再次双击）
18:40:59 "service start requested via start-dsh.vbs"
          [start-dsh] using port 3080 / [start-dsh] dsh not on PATH - npx via https://registry.npmmirror.com
          dsh web: http://127.0.0.1:3080
          ^C 终止批处理操作吗(Y/N)?                      ← npx 下载/启动中途被 Ctrl+C，cmd 卡死询问
```

### 2.3 残留进程证据（诊断期间实测）

- 18:40:59 启动的 **cmd PID 33008**（`dsh not on PATH - npx` 分支的 cmd 外壳）一直存活到 ~19:17 才被人工 taskkill。
- 18:41:51 启动的 **cmd PID 22072** 同样存活。
- **这两个 cmd 进程通过 `>> dsh.log 2>&1` 重定向持有 dsh.log 的文件句柄** → 这正是 34352 早期日志写入失败（静默）的最可能原因。
- taskkill 之后（~19:17），34352 的日志才写入成功（19:19:13 三条）。

### 2.4 `%TEMP%\dsh.log`（vbs 侧 fallback 日志）

- 最后写入时间 18:45:22，内容为 18:40 那次的 `[start-dsh] using port 3080 / dsh not on PATH - npx / dsh web: ... / ^C`。
- **19:15 的 vbs 运行后此处没有任何新增** → 佐证 vbs 在本轮**没有执行到写日志步骤**（或根本没被执行）。

---

## 三、代码路径分析

### 3.1 正常启动流水线（`LauncherApp.RunStartupAsync` + `Program`）

```
SplashForm.OnShown → RunPipelineAsync → LauncherApp.RunStartupAsync
  阶段0 BackgroundMaintenance（日志轮转/数据迁移/pending update 应用，可耗时 30-120s）
  CheckingInstance（单实例，Main 已处理）
  ResolvingRuntime
  探测端口 NeedsStart(3080)：
    端口已开 → "正在检查 dsh 服务…" → 直接进入 WaitingForReadiness（不调 vbs）
    端口未开 → "正在启动 dsh 服务…" → SweepStaleAndApplyUpdate → StartDshServiceViaVbs
  WaitingForReadiness："正在等待 dsh 服务就绪…" → WaitServiceReady 轮询 ≤180 次
    就绪 → "ready"；超时 → "timeout"（E2002）
```

### 3.2 `WaitServiceReady`（Program.cs:2221-2274）

- 每次循环：检查日志错误标志（`ShellLogic.LogShowsStartupError`，15s 宽限）→ `PortOpen(port)` TCP → `ShellLogic.IsHttpReady(url,http)` HTTP。
- 前 8 次 200ms，之后 1s，共 **180 次 ≈ 180s** 后 `poll: timeout after 180s` → 返回 `"timeout"`。

### 3.3 `StartDshServiceViaVbs`（Program.cs:1103-1126）

```csharp
var vbs = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
if (!File.Exists(vbs)) { Logger.Error("missing ...", E2001); return false; }
Process.Start(wscript.exe "vbs") { UseShellExecute=true };
```

### 3.4 vbs 启动分支（新版 start-dsh.vbs，18:51 构建）

```
where dsh 成功 → cmdline = "dsh web --host 127.0.0.1 --port 3080"
where dsh 失败 → 探测 %APPDATA%\npm\dsh.cmd
   存在 → 全路径调用（using npm shim）
   不存在 → npx -y --registry=DSH_NPM_MIRROR（默认 npmmirror）@deepseek-ai/dsh web ...
```

---

## 四、疑点清单（按可信度排序）

### 疑点 A（最可疑）：18:40 残留的 npx/cmd 进程导致 34352 误判「端口已开」
- 18:40 的 `dsh web` 曾打印 `http://127.0.0.1:3080`（服务进程可能仍活着监听），随后被 `^C` 打断，cmd 外壳挂死。
- 若 34352 在 19:15:21 启动时探测 3080 端口**仍被该残留服务占用** → `NeedsStart` 返回 false → **不调用 start-dsh.vbs**，直接进入 WaitingForReadiness 轮询那个「半死不活」的服务 → 180s 超时。
- 佐证：34352 无任何 `[start-dsh]`/`service start requested` 日志（vbs 从未被调起）；但诊断期间我们查看 3080 时已无监听，时间点可能介于残留服务退出与 34352 轮询之间，**需验证 19:15:21 时刻 3080 是否被占用**（事件日志/残留进程时间戳）。

### 疑点 B：single-file 发布下 `AppContext.BaseDirectory` 定位问题
- 用户跑的是 **single-file（PublishSingleFile=true）** 的 1.2MB exe；本地非 single-file 构建仅 177KB。
- `.NET 6+ single-file 中 AppContext.BaseDirectory 通常返回宿主 exe 目录`，但若运行时把原生资源（runtimes/win-x64/native）解压到临时目录、或宿主被 wscript 间接调用，`Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs")` 可能指向临时解压目录 → `File.Exists=false` → E2001。
- 但 E2001 日志也未出现（被日志锁掩盖），无法直接排除；**若 vbs 路径错，34352 应走 startOk=false 分支而非等待 180s**，此点与「等待了 180s」矛盾，故可信度低于 A。

### 疑点 C：日志文件被残留进程锁住 → 全链路诊断盲区
- 33008/22072 两个 cmd 用 `>>` 重定向持有 dsh.log 句柄，持续到 ~19:17 被杀。
- 期间 34352 的一切 `Logger.Info/Error`（E2001/E2002 除外后段）静默丢弃 → 我们看不到它在哪个阶段、vbs 是否被调、错误码是什么。
- 这是「为什么日志只有 3 条」的根因；**也说明本问题与 18:40 的 Ctrl+C 中断强相关**（用户上次会话留下了僵尸 cmd）。

### 疑点 D：pending dsh update（0.1.0-rc.7）反复尝试、E4002
- 17:45、18:17 两次启动都触发 `applying staged dsh update to 0.1.0-rc.7`，耗时 100-120 秒，其中 18:17 那次 **E4002 apply failed**。
- 19:15 的 34352 若在阶段 0 再次应用该 pending update（120s），会占用大量超时窗口，但 180s 超时是从 WaitingForReadiness 开始计，阶段 0 的耗时不计入——不过总体验仍可能是「用户觉得很久」。
- 需确认 19:15 启动前 `pending-update.json` 是否存在/内容；若存在且 apply 又失败，可能与 `%APPDATA%\npm` 全局 dsh 的权限/损坏有关。

### 疑点 E：`where dsh` 在 18:19/18:40 失败，但诊断时却成功
- 18:19、18:40 的 vbs 走了 `dsh not on PATH - npx` 分支，说明 wscript 继承的 PATH 里没有 npm 全局目录。
- 但我们当前 shell 里 `where dsh` 能找到 `C:\Users\enmusubi4\AppData\Roaming\npm\dsh` 且 shim 存在。
- **同一台机器不同进程的 PATH 不一致** → 需确认 DshWeb.exe 是否经「旧快捷方式/旧安装副本/提权」启动，导致 PATH 不含 `%APPDATA%\npm`。

---

## 五、建议验证步骤（交给更强模型前可先跑）

1. **查 19:15:21 时刻端口 3080 是否被占**：
   - 看残留进程 33008/22072 的父进程树与启动时刻（已杀，可查 `%TEMP%\dsh.log` 时间戳反推）。
2. **确认 pending-update.json 是否存在**：
   - `C:\Users\enmusubi4\.dsh\dsh-launcher\pending-update.json`，内容应为 `{"version":"0.1.0-rc.7",...}`。
3. **验证 single-file 的 BaseDirectory**：
   - 在 `scripts/build-portable.ps1` 产物上运行 `DshWeb.exe --ui-selftest` 或注入打印 `AppContext.BaseDirectory`，确认 start-dsh.vbs 能否被 `Path.Combine(BaseDirectory,...)` 找到。
4. **复现干净环境**：
   - 先 taskkill 全部残留 cmd/wscript/node，删除 `%TEMP%\dsh.log`，确保无僵尸进程后再双击，观察完整日志。
5. **检查是否有旧安装副本**：
   - 搜索全盘其他 `DshWeb.exe`（尤其 Program Files、快捷方式目标），确认双击的到底是哪个。

---

## 六、当前结论（一句话）

**最可能：18:40 那次被 Ctrl+C 中断的 npx 进程树（cmd 33008/22072）一直存活并锁住 dsh.log、且 3080 端口被其残留的 dsh 服务占用到 19:1x，导致 19:15 启动的新进程 34352 探测到「端口已开」→ 跳过 start-dsh.vbs → 对一只半死的服务轮询 180s → 超时退出（E2002）；而所有早期日志又因 dsh.log 被锁而静默丢失，造成「日志只有 3 行」的诊断盲区。** 次要候选：single-file 发布下 vbs 路径解析失败、pending update（0.1.0-rc.7）重复 apply 失败拖慢启动。
