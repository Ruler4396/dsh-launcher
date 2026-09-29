# Reverse-check the new static gate: it must go RED when TrayExitRequested is absent.
$ErrorActionPreference = 'Stop'
$src = Get-Content 'E:\dsh-launcher\src\DshShell\Program.cs' -Raw
$rx = 'ShouldStopServiceOnClose\([^;]*TrayExitRequested'
Write-Host ("real source matches = {0} (expect True)" -f ($src -match $rx))
# 造一条"漏传参数"的假源码：把实参列表里的 TrayExitRequested 那一行删掉
$doctored = $src -replace '(?s)SessionApp\?\.ServiceStartedByShell == true,\s*\r?\n\s*WindowManager\.Instance\.TrayExitRequested', 'SessionApp?.ServiceStartedByShell == true'
Write-Host ("doctored (missing arg) matches = {0} (expect False)" -f ($doctored -match $rx))
Write-Host ("doctored differs from real = {0} (expect True)" -f ($doctored -ne $src))

# ---- 标题栏接管点计数闸：反向验红 ----
$ctb = Get-Content 'E:\dsh-launcher\src\DshShell\Chrome\CustomTitleBar.cs' -Raw
$rx2 = 'SendMessage\s*\([^;]*Win32Constants\.WM_NCLBUTTONDOWN'
$n = ([regex]::Matches($ctb, $rx2)).Count
Write-Host ("real SendMessage(WM_NCLBUTTONDOWN) sites = {0} (expect 1)" -f $n)
# 反向验红分两条：① 多一处接管点 → 计数闸必须变 2；② 把阈值判定删掉（旧行为是"回到
# MouseDown 里直接接管"，那会同时删掉 ShouldStartCaptionDrag）→ 阈值闸必须变 False。
$extraSite = $ctb + "`r`n// drill`r`nclass _Drill { void M() { NativeMethods.SendMessage(h, 0, (IntPtr)Win32Constants.WM_NCLBUTTONDOWN, IntPtr.Zero); } }"
Write-Host ("doctored (+1 takeover site) count = {0} (expect 2)" -f ([regex]::Matches($extraSite, $rx2)).Count)
$noThreshold = $ctb -replace 'ShouldStartCaptionDrag', 'X'
Write-Host ("doctored (threshold removed) gate = {0} (expect False)" -f ($noThreshold -match 'ShouldStartCaptionDrag'))
Write-Host ("threshold gate present = {0} (expect True)" -f ($ctb -match 'ShouldStartCaptionDrag'))

# ---- (D) DPI 几何单一所有者闸：反向验红 ----
$form = Get-Content 'E:\dsh-launcher\src\DshShell\Windows\DshShellForm.cs' -Raw
Write-Host ("form uses RescaleWindowForDpi = {0} (expect True)" -f ($form -match 'RescaleWindowForDpi'))
$dpiHandlers = ([regex]::Matches($src, '\.DpiChanged\s*\+=')).Count
Write-Host ("composition-root DpiChanged handlers = {0} (expect 0)" -f $dpiHandlers)
# 造回"组合根自己挂处理器"的旧结构 → 计数必须变 1，否则这条闸抓不到回归
$withHandler = $src + "`r`n// drill`r`nform.DpiChanged += (_, _) => { };"
Write-Host ("doctored (+1 handler) count = {0} (expect 1)" -f ([regex]::Matches($withHandler, '\.DpiChanged\s*\+=')).Count)
$noRescale = $form -replace 'RescaleWindowForDpi', 'X'
Write-Host ("doctored (rescale removed) gate = {0} (expect False)" -f ($noRescale -match 'RescaleWindowForDpi'))

# ---- 通知裁决唯一入口闸：反向验红 ----
$mgr = Get-Content 'E:\dsh-launcher\src\DshShell\Managers\DshUpdateManager.cs' -Raw
$rxOut = 'new ShellLogic\.UpdateNoticeFlowPolicy\.Outcome\('
$rxDec = 'UpdateNoticeFlowPolicy\.Decide\s*\('
Write-Host ("real Outcome ctors = {0} (expect 1); Decide calls = {1} (expect 1)" -f `
    ([regex]::Matches($mgr, $rxOut)).Count, ([regex]::Matches($mgr, $rxDec)).Count)
# 造回旧形状：假信号分支自己 return 一个结论（多一处 Outcome）→ 计数必须变 2
$lying = $mgr + "`r`n// drill`r`nstatic object _ => new ShellLogic.UpdateNoticeFlowPolicy.Outcome(null, null, null, `"x`", `"y`", `"test-hook`");"
Write-Host ("doctored (hook returns its own verdict) ctors = {0} (expect 2)" -f ([regex]::Matches($lying, $rxOut)).Count)
$noDecide = $mgr -replace 'UpdateNoticeFlowPolicy\.Decide', 'X'
Write-Host ("doctored (Decide removed) calls = {0} (expect 0)" -f ([regex]::Matches($noDecide, $rxDec)).Count)

# ---- 跳过状态入参形状闸：反向验红 ----
$rxSkipArg = 'Decide\([^;]*skippedVersion:\s*StagedUpdate\.ReadSkippedDshVersion'
Write-Host ("real passes skippedVersion string = {0} (expect True)" -f ($mgr -match $rxSkipArg))
$intForm = $mgr -replace 'skippedVersion: StagedUpdate\.ReadSkippedDshVersion\(\)', 'skippedVersion: 0'
Write-Host ("doctored (int verdict passed) = {0} (expect False)" -f ($intForm -match $rxSkipArg))
$logic = Get-Content 'E:\dsh-launcher\src\DshShell\ShellLogic.cs' -Raw
$sig = [regex]::Match($logic, 'public static Outcome Decide\([^)]*\)')
Write-Host ("Decide signature found = {0} (expect True); has int param = {1} (expect False)" -f `
    $sig.Success, ($sig.Value -match '\bint\b'))
$oldSig = $sig.Value -replace 'string\? skippedVersion', 'int latestVsSkipped'
Write-Host ("doctored old signature has int param = {0} (expect True)" -f ($oldSig -match '\bint\b'))

# ---- 缺陷 H/I 的三条新闸：反向验红（只在内存里造案，不动真文件） ----
$rxReceipt = 'ArmedNotice|关闭本提示后重新打开'
Write-Host ("H/I real Program.cs carries the receipt promise = {0} (expect False)" -f ($src -match $rxReceipt))
$withReceipt = $src + "`r`n// drill`r`n// 关闭本提示后重新打开 dsh-launcher"
Write-Host ("H/I doctored (receipt sentence back) = {0} (expect True)" -f ($withReceipt -match $rxReceipt))
$withArmedConst = $src + "`r`n// drill`r`nShowError(code, ShellLogic.StartupFailureRecoveryPolicy.ArmedNotice);"
Write-Host ("H/I doctored (receipt const referenced) = {0} (expect True)" -f ($withArmedConst -match $rxReceipt))

$rxNoRetry = 'StartupStep\.RetryInSafeMode'
Write-Host ("H real Program.cs retries in-process = {0} (expect True)" -f ($src -match $rxNoRetry))
$noRetry = $src -replace 'StartupStep\.RetryInSafeMode', 'StartupStep.Fail'
Write-Host ("H doctored (retry step removed) = {0} (expect False)" -f ($noRetry -match $rxNoRetry))

$la = Get-Content 'E:\dsh-launcher\src\DshShell\LauncherApp.cs' -Raw
$rxHardCode = 'Logger\.Error\(\$"[^"]*service readiness failed[^;]*ErrorCodes\.E20'
$rxSameSource = 'service readiness failed[\s\S]{0,160}MapVerdictErrorCode'
Write-Host ("I real LauncherApp hardcodes an E20xx at the log site = {0} (expect False)" -f ($la -match $rxHardCode))
Write-Host ("I real log code comes from MapVerdictErrorCode = {0} (expect True)" -f ($la -match $rxSameSource))
$preFix = $la -replace '(?s)Logger\.Error\(\$"?service readiness failed.*?MapVerdictErrorCode\(waitResult\)\);', `
    'Logger.Error($"service readiness failed: {waitResult}", ErrorCodes.E2002);'
Write-Host ("I doctored (pre-fix one-liner) trips the hardcode gate = {0} (expect True)" -f ($preFix -match $rxHardCode))
Write-Host ("I doctored breaks the same-source gate = {0} (expect False)" -f ($preFix -match $rxSameSource))
Write-Host ("I doctored differs from real = {0} (expect True)" -f ($preFix -ne $la))
