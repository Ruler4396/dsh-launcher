' DeepSeek Harness silent launcher (no window). Used by autostart / one-click entry.
' Starts `dsh web` and writes log to the unified log file.
' Prefers a globally installed `dsh`; falls back to `npx -y @deepseek-ai/dsh` so the
' service still starts for users who never ran `npm install -g @deepseek-ai/dsh`.
'
' NOTE: keep this file pure ASCII. VBScript reads .vbs as ANSI; non-ASCII
' comment bytes can swallow the following newline and comment out real code.
'
' Port passthrough: the dsh-launcher shell sets DSH_PORT (process env var)
' before spawning us; children inherit it. Unset -> default 3080.
'
' v0.3.0 unified log: the shell sets DSH_LOG to DSH_HOME\dsh-launcher\dsh.log
' (single log file shared with the shell's JSON Lines; append mode 8 - the shell
' owns rotation, this script never truncates or rolls over).
' If the log is locked by a running service or missing, fall back to %TEMP% so the
' service still starts: a dead cold start is worse than a missing log.
Set sh = CreateObject("WScript.Shell")
port = "3080"
Set env = sh.Environment("PROCESS")
If env("DSH_PORT") <> "" Then port = env("DSH_PORT")

Set fso = CreateObject("Scripting.FileSystemObject")
logfile = env("DSH_LOG")
If logfile = "" Then
    If env("DSH_HOME") <> "" Then dshhome = env("DSH_HOME") Else dshhome = sh.ExpandEnvironmentStrings("%USERPROFILE%") & "\.dsh"
    logfile = dshhome & "\dsh-launcher\dsh.log"
End If
' 确保父目录存在（创建目录 + 防御性打开文件）。OpenTextFile 的第三个参数 True
' 只创建文件*不创建目录*，目录不存在时直接失败。此前无此处理导致：
' 1) 旧版 autostart 直接拉起本脚本时 %USERPROFILE%\.dsh\dsh-launcher\ 尚未创建，
'    失败后 f 为 Empty（非 Nothing），f Is Nothing 引发"缺少对象"(800A01A8) 弹窗；
' 2) On Error Resume Next 静默吞掉该错误使回退到 %TEMP% 的分支不执行。
' 先创建目录，再显式初始化 f 为 Nothing，确保无论哪种失败路径结果都正确。
On Error Resume Next
fso.CreateFolder fso.GetParentFolderName(logfile)
On Error GoTo 0
Set f = Nothing
On Error Resume Next
Set f = fso.OpenTextFile(logfile, 8, True)
If f Is Nothing Then
    logfile = sh.ExpandEnvironmentStrings("%TEMP%") & "\dsh.log"
    Set f = fso.OpenTextFile(logfile, 8, True)
End If
On Error GoTo 0
If Not (f Is Nothing) Then
    f.WriteLine "[start-dsh] using port " & port
    f.Close
End If

' --no-open: dsh-launcher 壳自行管理 WebView2 窗口，不需要 dsh 本体打开系统浏览器。
' 冷启动时系统浏览器自动打开的根因：dsh web 默认调用 ShellExecute 打开浏览器，
' 由 start-dsh.vbs 作为子进程继承后触发。添加 --no-open 抑制该行为。
' ADR-022 安全模式：壳注入 DSH_PROFILE=.dsh-safe 时，用根级 `--profile <name>` 启动
' （隔离 profile，剥离第三方插件）；否则默认 `web` 子命令。
bootMode = "web"
If env("DSH_PROFILE") <> "" Then bootMode = "--profile " & env("DSH_PROFILE")
If sh.Run("cmd /c where dsh >nul 2>&1", 0, True) = 0 Then
    cmdline = "dsh " & bootMode & " --host 127.0.0.1 --port " & port & " --no-open"
    note = "using global dsh"
Else
    ' where dsh 失败未必是"没装"——%APPDATA%\npm 可能不在当前 PATH（新装的 npm 全局包，
    ' 或启动器由旧环境变量启动），但 dsh.cmd 实际存在。先探测 npm 全局 shim 用全路径调用，
    ' 避免直接落 npx 网络下载导致"卡在等待服务就绪"（2026-08 用户复现：dsh not on PATH -
    ' npx via npmmirror，npx 解析/下载包极慢甚至挂起）。
    q2 = Chr(34)
    npmShim = sh.ExpandEnvironmentStrings("%APPDATA%") & "\npm\dsh.cmd"
    If fso.FileExists(npmShim) Then
        cmdline = q2 & npmShim & q2 & " " & bootMode & " --host 127.0.0.1 --port " & port & " --no-open"
        note = "using npm shim " & npmShim
    Else
        ' 真没装：dsh 本体经 npx 从 npm registry 下载，国内/弱网访问默认 npmjs 慢易超时。
        ' 镜像回退：DSH_NPM_MIRROR 显式指定则用（封装 npm 私有 registry 场景）；
        ' 未指定时默认 npmmirror（公共 npm 镜像，国内可直连、全球亦快，@deepseek-ai/dsh 为公共包）。
        npmRegistry = env("DSH_NPM_MIRROR")
        If npmRegistry = "" Then npmRegistry = "https://registry.npmmirror.com"
        cmdline = "npx -y --registry=" & npmRegistry & " " & bootMode & " --host 127.0.0.1 --port " & port & " --no-open"
        note = "dsh not installed - npx via " & npmRegistry
    End If
End If
' 日志重定向目标必须加引号：USERPROFILE 含空格（如 C:\Users\John Smith）时，
' 不带引号会把路径截断成命令参数/碎片文件名（实测复现）；含 & 等 cmd 元字符时可注入命令。
q = Chr(34)
sh.Run "cmd /c ""echo [start-dsh] " & note & " >> " & q & logfile & q & " && " & cmdline & " >> " & q & logfile & q & " 2>&1""", 0, False
