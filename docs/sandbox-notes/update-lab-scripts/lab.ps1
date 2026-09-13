# ============================================================================
# lab.ps1 - dsh manual-update sandbox lab (host-safe)
#
# One-click lifecycle for testing the launcher's MANUAL UPDATE flow against a
# local fake npm registry, without touching the host machine's dsh/launcher:
#
#   setup   - build/refresh lab infra (app copy, node.exe, snapshot, tarballs)
#   start   - launch the sandbox launcher (optionally -Reset to snap back to baseline)
#   reset   - STOP lab processes + instantly restore baseline (local file ops only)
#   stop    - stop lab processes (launcher / fake-dsh / registry) only
#   status  - show current lab state
#
# Scenario mirrors production reality:
#   installed baseline = 0.1.0-rc.6, registry "latest" = 0.1.1-rc.2
#
# Isolation model (why the host is safe):
#   app runs from lab\app COPY      -> host dist\dev exe never launched/killed
#   DSH_SANDBOX=1                   -> launcher skips autostart/ProgramData cleanup
#   DSH_HOME=<lab>\home             -> runtimes/staging/pending/logs all inside lab
#   DSH_WEB_PORT=3999               -> service port != host 3080; mutex differs
#   DSH_NPM_REGISTRY/MIRROR=4873    -> version check + npm/pnpm hit local registry
#   DSH_WEBVIEW2_DATA=<lab>\webview2-> WebView2 profile isolated from host
#   inherited DSH_*/FAKE_DSH* env   -> CLEARED before lab values are applied
#                                      (harness injects e.g. DSH_WEB_URL=3080!)
#   PATH prepends sandbox node      -> node/npm resolution stays in lab
#
# Kills are TARGETED: only PIDs we started (pid file), verified against the LAB
# APP COPY, or identified by the lab-only WebView2 user-data-dir marker.
# INCIDENT FIX 2026-08-22: an earlier revision swept DshWeb.exe by exe path equal
# to dist\dev\DshWeb.exe, which IS the host launcher binary -> host launcher got
# killed. Now no kill path ever references a host location. Port 3080 (host dsh)
# is never touched.
# ============================================================================

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('setup', 'start', 'reset', 'stop', 'status', 'inject-badplugin')]
    [string]$Command = 'status',
    [switch]$Reset,          # -Reset with start: force clean baseline before launching
    [switch]$ReuseStash,     # start: restore staged target build from stash (skip download)
    [switch]$NoStop,         # reset: do not stop processes first
    [switch]$SafeModeAutoYes # start: DSH_TEST_SAFE_MODE_ANSWER=yes (auto-answer safe-mode modal)
)

$ErrorActionPreference = 'Stop'

# ------------------------------ constants -----------------------------------
$Lab        = $PSScriptRoot
$Sandbox    = Split-Path -Parent $Lab                      # E:\dsh-lab\sandbox
# Lab was relocated out of the repo on 2026-08-24 (repo sandbox/ -> E:\dsh-lab\sandbox),
# so the host repo root can no longer be derived by walking up; pin it explicitly.
$RepoRoot   = 'E:\dsh-launcher'                            # host repo (reference only - never touched)
$LabHome    = Join-Path $Lab 'home'
$DataDir    = Join-Path $LabHome 'dsh-launcher'
$Runtimes   = Join-Path $DataDir 'runtimes'
$Staging    = Join-Path $DataDir 'staging'
$Pending    = Join-Path $DataDir 'pending-update.json'
$Skipped    = Join-Path $DataDir 'skipped-update.json'
$Stash      = Join-Path $Lab 'stash'
$RegistryDir= Join-Path $Lab 'registry'
$TarballDir = Join-Path $RegistryDir 'tarballs'
$PidDir     = Join-Path $Lab 'pid'
$LogsDir    = Join-Path $Lab 'logs'
$WebviewDir = Join-Path $Lab 'webview2'
$NpmCache   = Join-Path $Lab 'npm-cache'
$NodeDir    = Join-Path $Sandbox 'env\node'
$NodeExe    = Join-Path $NodeDir 'node.exe'
$NpmCliJs   = Join-Path $NodeDir 'node_modules\npm\bin\npm-cli.js'
# [INCIDENT FIX] sandbox runs its OWN copy of the app under lab\app.
$AppDir     = Join-Path $Lab 'app'
$Launcher   = Join-Path $AppDir 'DshWeb.exe'
$HostLauncherPath = Join-Path $RepoRoot 'dist\dev\DshWeb.exe'   # reference only - never touched
$RegServer  = Join-Path $Lab 'registry-server.js'
$FakeDsh    = Join-Path $Lab 'fake-dsh.js'
$PkgRoot    = Join-Path $Sandbox 'fake-registry\tarballs'  # package-<version> source dirs

# Scenario versions (mirror production: baseline rc6 -> current latest 0.1.1-rc.2)
$Baseline   = '0.1.0-rc.6'
$Intermediate = '0.1.0-rc.7'
$Target     = '0.1.1-rc.2'
$Versions   = @($Baseline, $Intermediate, $Target)

# Lab mode:
#   real - REAL @deepseek-ai/dsh packages; local registry proxies metadata from
#          npmmirror/npmjs and streams real tarballs through a disk cache. The
#          baseline runtime is a full production-style install (300+ deps).
#   fake - zero-dep fake packages; instant cycles for pure mechanics tests.
$LabMode    = 'real'

$SvcPort    = 3999
$RegPort    = 4873
$HostPort   = 3080     # host dsh (this GUI) - NEVER touch

$PidLauncher= Join-Path $PidDir 'launcher.pid'
$PidRegistry= Join-Path $PidDir 'registry.pid'
$EnvCmd     = Join-Path $PidDir 'run-launcher-env.cmd'

function Info($m)  { Write-Host "[lab] $m" -ForegroundColor Cyan }
function Ok($m)    { Write-Host "  [OK] $m" -ForegroundColor Green }
function Warn($m)  { Write-Host "  [!!] $m" -ForegroundColor Yellow }
function Fail($m)  { Write-Host "  [XX] $m" -ForegroundColor Red }
function Step($m)  { Write-Host "`n== $m ==" -ForegroundColor White }

function PkgSrc([string]$Version) { Join-Path $PkgRoot ("package-" + $Version) }

# --------------------------- small utilities --------------------------------

function Get-ProcInfo([int]$ProcId) {
    if ($ProcId -le 0) { return $null }
    try {
        return Get-CimInstance Win32_Process -Filter "ProcessId=$ProcId" `
            -Property ProcessId, Name, ExecutablePath, CommandLine -ErrorAction Stop
    } catch { return $null }
}

function Get-PortOwnerPid([int]$Port) {
    try {
        $c = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop |
             Select-Object -First 1
        if ($c) { return [int]$c.OwningProcess }
    } catch { }
    return 0
}

function Test-HttpOk([string]$Url, [int]$TimeoutSec = 2) {
    try {
        $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $TimeoutSec
        return ($r.StatusCode -ge 200 -and $r.StatusCode -lt 500)
    } catch { return $false }
}

function Get-ServedVersion {
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$SvcPort/health" -UseBasicParsing -TimeoutSec 2
        $j = $r.Content | ConvertFrom-Json
        if ($j.version) { return [string]$j.version }
    } catch { }
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$SvcPort/" -UseBasicParsing -TimeoutSec 3
        if ($r.Content -match 'Version:\s*([0-9][^\s<]+)') { return $Matches[1] }          # fake page
    } catch { }
    # Real dsh serves an SPA with no version in HTML - read it from the service
    # process command line (node ...runtimes\<version>\...\lib\bin.js).
    try {
        $owner = Get-PortOwnerPid $SvcPort
        if ($owner -gt 0) {
            $cl = (Get-CimInstance Win32_Process -Filter "ProcessId=$owner").CommandLine
            if ($cl -match 'runtimes\\([^\\]+)\\') { return $Matches[1] }
        }
    } catch { }
    return $null
}

function Compare-LabVersion([string]$A, [string]$B) {
    function core([string]$v) {
        $d = $v.IndexOf('-'); if ($d -ge 0) { return $v.Substring(0, $d) } else { return $v }
    }
    function pre([string]$v) {
        $d = $v.IndexOf('-'); if ($d -ge 0) { return $v.Substring($d + 1) } else { return '' }
    }
    $ca = core $A -split '\.'; $cb = core $B -split '\.'
    for ($i = 0; $i -lt [Math]::Max($ca.Count, $cb.Count); $i++) {
        $xa = 0; $xb = 0
        if ($i -lt $ca.Count) { [void][int]::TryParse($ca[$i], [ref]$xa) }
        if ($i -lt $cb.Count) { [void][int]::TryParse($cb[$i], [ref]$xb) }
        if ($xa -ne $xb) { return ($xa - $xb) }
    }
    $pa = pre $A; $pb = pre $B
    if ($pa -eq '' -and $pb -eq '') { return 0 }
    if ($pa -eq '') { return 1 }
    if ($pb -eq '') { return -1 }
    return [string]::CompareOrdinal($pa, $pb)
}

# ------------------------- host-safety assertions ---------------------------

function Assert-HostSafety {
    if ($SvcPort -eq $HostPort -or $RegPort -eq $HostPort) {
        throw "lab port collides with host dsh port $HostPort - refusing to run"
    }
    if (-not ($Lab -like "$Sandbox*")) { throw "lab path unexpected: $Lab" }
    # [INCIDENT FIX] hard guards: sandbox launcher must be the LAB COPY, never dist\
    if ($Launcher -notlike "$AppDir*") { throw "sandbox launcher path unexpected: $Launcher" }
    if ($Launcher -like "$RepoRoot\dist*") { throw "refusing: sandbox launcher must live in lab\app, not dist\" }
    if (-not (Test-Path (PkgSrc $Baseline))) { throw "missing fake package source: $(PkgSrc $Baseline)" }
    if (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'dsh-launcher'))) {
        # informational only; legacy migration only touches loose FILES at that root
    }
    $legacy = Join-Path $env:LOCALAPPDATA 'dsh-launcher'
    if (Test-Path $legacy) {
        $loose = @(Get-ChildItem -Path $legacy -File -ErrorAction SilentlyContinue)
        if ($loose.Count -gt 0) {
            Warn ("%LOCALAPPDATA%\dsh-launcher has {0} loose file(s); launcher legacy-migrates loose root files. Files: {1}" -f $loose.Count, (($loose | Select-Object -First 5 -ExpandProperty Name) -join ', '))
        }
    }
}

# Visibility-only: list DshWeb processes that are NOT the lab copy. Never kills.
function Show-ForeignLaunchers {
    try {
        $foreign = @(Get-Process DshWeb -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $Launcher })
        foreach ($f in $foreign) {
            Ok ("host launcher detected: pid={0} path={1} - lab will NOT touch it" -f $f.Id, $f.Path)
        }
    } catch { }
}

# ------------------------------ targeted stops ------------------------------

function Stop-Launcher {
    # [INCIDENT FIX 2026-08-22] Kill candidates are ONLY:
    #   1. the PID recorded in our pid file, verified as the LAB app copy;
    #   2. a DshWeb whose WebView2 children carry --user-data-dir="<lab>\webview2"
    #      (marker only OUR instance can have; host uses %LOCALAPPDATA%\DshWeb).
    # NO path-based sweep. Host launcher processes are never candidates.
    $stopped = $false
    if (Test-Path $PidLauncher) {
        $pid0 = 0
        [void][int]::TryParse((Get-Content $PidLauncher -ErrorAction SilentlyContinue), [ref]$pid0)
        $p = Get-ProcInfo $pid0
        if ($p -and $p.Name -eq 'DshWeb.exe' -and $p.ExecutablePath -eq $Launcher) {
            Info ("stopping sandbox launcher pid={0} (pid file)" -f $pid0)
            cmd /d /c "taskkill /PID $pid0 /T /F" *> $null
            $stopped = $true
        } elseif ($p) {
            Warn ("pid-file pid={0} is not the lab launcher (name={1} path={2}); NOT killing" -f $pid0, $p.Name, $p.ExecutablePath)
        }
        Remove-Item $PidLauncher -Force -ErrorAction SilentlyContinue
    }
    if (-not $stopped) {
        try {
            $kids = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -and $_.CommandLine.Contains($WebviewDir) })
            $candPids = @($kids | ForEach-Object { $_.ParentProcessId } | Sort-Object -Unique)
            foreach ($cp in $candPids) {
                $p = Get-ProcInfo $cp
                if ($p -and $p.Name -eq 'DshWeb.exe' -and $p.ExecutablePath -eq $Launcher) {
                    Info ("stopping sandbox launcher pid={0} (lab webview2 marker)" -f $cp)
                    cmd /d /c "taskkill /PID $cp /T /F" *> $null
                    $stopped = $true
                }
            }
        } catch { }
    }
    if (-not $stopped) { Ok "sandbox launcher not running" }
}

function Stop-FakeService {
    $svcPid = Get-PortOwnerPid $SvcPort
    if ($svcPid -le 0) { Ok "no service on port $SvcPort"; return }
    $p = Get-ProcInfo $svcPid
    # Kill requires BOTH: executable inside sandbox tree AND a lab marker.
    $inSandbox = ($p -and $p.ExecutablePath -and
        $p.ExecutablePath.StartsWith($Sandbox, [System.StringComparison]::OrdinalIgnoreCase))
    $isOurs = ($inSandbox -and (
        ($p.ExecutablePath -and $p.ExecutablePath.StartsWith($NodeDir, [System.StringComparison]::OrdinalIgnoreCase)) -or
        ($p.CommandLine -and $p.CommandLine -like '*fake-dsh.js*')
    ))
    if ($isOurs) {
        Info ("stopping fake-dsh service pid={0} on port {1}" -f $svcPid, $SvcPort)
        cmd /d /c "taskkill /PID $svcPid /T /F" *> $null
    } else {
        Warn ("port {0} owned by pid={1} which is NOT lab fake-dsh - NOT touching it" -f $SvcPort, $svcPid)
    }
}

function Stop-Registry {
    $stopped = $false
    if (Test-Path $PidRegistry) {
        $pid0 = 0
        [void][int]::TryParse((Get-Content $PidRegistry -ErrorAction SilentlyContinue), [ref]$pid0)
        $p = Get-ProcInfo $pid0
        if ($p -and $p.CommandLine -and $p.CommandLine -like '*registry-server.js*') {
            Info ("stopping fake registry pid={0}" -f $pid0)
            cmd /d /c "taskkill /PID $pid0 /T /F" *> $null
            $stopped = $true
        }
        Remove-Item $PidRegistry -Force -ErrorAction SilentlyContinue
    }
    $owner = Get-PortOwnerPid $RegPort
    if ($owner -gt 0) {
        $p = Get-ProcInfo $owner
        $inSandbox = ($p -and $p.ExecutablePath -and
            $p.ExecutablePath.StartsWith($Sandbox, [System.StringComparison]::OrdinalIgnoreCase))
        if ($inSandbox -and $p.CommandLine -and $p.CommandLine -like '*update-lab*registry-server.js*') {
            Info ("stopping fake registry pid={0} (port sweep)" -f $owner)
            cmd /d /c "taskkill /PID $owner /T /F" *> $null
            $stopped = $true
        } else {
            Warn ("port {0} owned by pid={1} which is not our registry - NOT touching it" -f $RegPort, $owner)
        }
    }
    if (-not $stopped) { Ok "fake registry not running" }
}

function Stop-Lab {
    Stop-Launcher
    Stop-FakeService
    Stop-Registry
}

# ------------------------------ infra: setup --------------------------------

function Ensure-NodeExe {
    if (Test-Path $NodeExe) { return }
    $hostPortable = Join-Path $env:LOCALAPPDATA 'dsh-launcher\env\node\node.exe'
    if (-not (Test-Path $hostPortable)) { throw "no node.exe in lab and host portable node missing: $hostPortable" }
    New-Item -ItemType Directory -Force -Path $NodeDir | Out-Null
    Copy-Item $hostPortable $NodeExe -Force   # READ from host, WRITE into lab only
    Ok "copied node.exe into lab (host file untouched)"
}

function Ensure-PackageSource([string]$Version) {
    $src = PkgSrc $Version
    $binDir = Join-Path $src 'bin'
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null
    $pkgJson = Join-Path $src 'package.json'
    if (-not (Test-Path $pkgJson)) {
        @{ name = '@deepseek-ai/dsh'; version = $Version;
           description = 'Fake dsh for sandbox testing';
           bin = @{ dsh = 'bin/fake-dsh.js' }; main = 'bin/fake-dsh.js' } |
            ConvertTo-Json | Set-Content -NoNewline $pkgJson -Encoding UTF8
    }
    Copy-Item $FakeDsh (Join-Path $binDir 'fake-dsh.js') -Force
}

function Sync-FakeDshSources {
    foreach ($v in $Versions) { Ensure-PackageSource $v }
    Ok ("fake-dsh.js v3 synced into package sources: {0}" -f ($Versions -join ', '))
}

function Get-SnapshotDir {
    # Mode-scoped so fake and real baselines can coexist on disk.
    return Join-Path $Lab ("snapshot\" + $LabMode + "\runtime-" + $Baseline)
}

function Build-Snapshot {
    $snapshot = Get-SnapshotDir
    if ($LabMode -eq 'real') {
        $src = Join-Path $Runtimes $Baseline
        if (-not (Test-RuntimeComplete $src $Baseline)) { throw "real baseline missing - seed first (setup)" }
        New-Item -ItemType Directory -Force -Path $snapshot | Out-Null
        robocopy $src $snapshot /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        if (-not (Test-RuntimeComplete $snapshot $Baseline)) { throw "real snapshot incomplete" }
        Ok "pristine REAL $Baseline snapshot at $snapshot"
    } else {
        $pkgDir = Join-Path $snapshot 'node_modules\@deepseek-ai\dsh'
        New-Item -ItemType Directory -Force -Path (Join-Path $pkgDir 'bin') | Out-Null
        Copy-Item (Join-Path (PkgSrc $Baseline) 'package.json') (Join-Path $pkgDir 'package.json') -Force
        Copy-Item $FakeDsh (Join-Path $pkgDir 'bin\fake-dsh.js') -Force
        Ok "pristine $Baseline snapshot at $snapshot"
    }
}

function Build-Tarballs {
    New-Item -ItemType Directory -Force -Path $TarballDir | Out-Null
    $env:npm_config_cache = $NpmCache
    foreach ($v in $Versions) {
        $out = Join-Path $TarballDir ("deepseek-ai-dsh-{0}.tgz" -f $v)
        if (Test-Path $out) { Remove-Item $out -Force }
        & $NodeExe $NpmCliJs pack (PkgSrc $v) --pack-destination "$TarballDir" --no-audit --no-fund --loglevel=error *> (Join-Path $LogsDir 'npm-pack.log')
        if (-not (Test-Path $out)) { throw "npm pack failed for $v (see $LogsDir\npm-pack.log)" }
        Ok ("packed {0}" -f (Split-Path $out -Leaf))
    }
}

function Write-RegistryConfig {
    New-Item -ItemType Directory -Force -Path $RegistryDir | Out-Null
    @{ mode = $LabMode; port = $RegPort; latest = $Target; versions = $Versions } |
        ConvertTo-Json | Set-Content -NoNewline (Join-Path $RegistryDir 'config.json') -Encoding ASCII
    Ok "registry config: mode=$LabMode latest=$Target versions=[$($Versions -join ', ')]"
}

function Copy-DshProfileData {
    # Real dsh needs its ecosystem data (profiles/plugins/storages/settings) to
    # boot. Copy CONFIG pieces from the host ~/.dsh into the lab home - READ from
    # host, WRITE into lab only. Excluded on purpose: sessions (privacy+size),
    # dsh-launcher (launcher's own DataDir), credentials* (secrets), node_modules
    # (junction farms pointing at the HOST runtime; real dsh re-links on boot).
    $srcRoot = Join-Path $env:USERPROFILE '.dsh'
    if (-not (Test-Path $srcRoot)) { Warn "no host .dsh at $srcRoot - skipping profile sync"; return }
    robocopy (Join-Path $srcRoot 'profiles') (Join-Path $LabHome 'profiles') /E /XD node_modules /XF '*.log' /NFL /NDL /NJH /NJS /NP | Out-Null
    foreach ($item in @('settings.yaml', 'plugins', 'storages', '.agent-presets')) {
        $src = Join-Path $srcRoot $item
        if (-not (Test-Path $src)) { continue }
        if ((Get-Item $src).PSIsContainer) {
            robocopy $src (Join-Path $LabHome $item) /E /XD node_modules /NFL /NDL /NJH /NJS /NP | Out-Null
        } else {
            Copy-Item $src (Join-Path $LabHome $item) -Force
        }
    }
    Ok "dsh profile/config data synced into lab home (host read-only)"
}

function Seed-RealBaseline {
    # Install the REAL baseline package the same way the launcher builds updates:
    # tarball + pnpm hoisted install. Result becomes runtimes\<Baseline> and the
    # instant-restore snapshot source.
    $cache = Join-Path $RegistryDir 'tarball-cache'
    New-Item -ItemType Directory -Force -Path $cache, $LogsDir | Out-Null
    $tgz = Join-Path $cache ("deepseek-ai-dsh-{0}.tgz" -f $Baseline)
    if (-not (Test-Path $tgz)) {
        Info "downloading REAL $Baseline tarball from npmmirror..."
        Invoke-WebRequest -Uri "https://registry.npmmirror.com/@deepseek-ai/dsh/-/dsh-$Baseline.tgz" `
            -OutFile $tgz -TimeoutSec 300
        Ok ("downloaded {0:N1} MB" -f ((Get-Item $tgz).Length / 1MB))
    } else {
        Ok "baseline tarball cached"
    }
    $seedBuild = Join-Path $LogsDir 'seed-build'
    if (Test-Path $seedBuild) { Remove-Item $seedBuild -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $seedBuild | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $seedBuild 'package.json'),
        '{"name":"dsh-runtime-seed","version":"1.0.0","private":true}')
    $pnpm = "$env:APPDATA\npm\node_modules\pnpm\bin\pnpm.cjs"
    $env:npm_config_cache = $NpmCache
    $env:COREPACK_ENABLE_DOWNLOAD_PROMPT = '0'
    Info "pnpm-installing REAL runtime (300+ deps, minutes on first run)..."
    Push-Location $seedBuild
    try {
        & $NodeExe $pnpm install $tgz --ignore-workspace --config.node-linker=hoisted `
            '--registry=https://registry.npmmirror.com' 2>&1 | Select-Object -Last 4 | Write-Host
    } finally { Pop-Location }
    if (-not (Test-RuntimeComplete $seedBuild $Baseline)) { throw "real seed build incomplete: $seedBuild" }
    $dest = Join-Path $Runtimes $Baseline
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Move-Item $seedBuild $dest
    Ok "real baseline installed: $dest"
}

function Copy-AppToLab {
    # [INCIDENT FIX] Mirror the app into lab\app so the sandbox NEVER runs the
    # host launcher exe (dist\dev). Read from dist, write into lab only.
    # DSH_LAB_APP_SOURCE env overrides the source dir (e.g. a candidate build
    # under dist\dev-toastinterop) WITHOUT ever touching the host launcher.
    $srcDir = $env:DSH_LAB_APP_SOURCE
    if (-not $srcDir) { $srcDir = Split-Path -Parent $HostLauncherPath }
    if (-not (Test-Path (Join-Path $srcDir 'DshWeb.exe'))) { throw "app source not found: $srcDir" }
    New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
    robocopy $srcDir $AppDir /MIR /XF DshWeb.pdb *.xml /NFL /NDL /NJH /NJS /NP | Out-Null
    if (-not (Test-Path $Launcher)) { throw "app copy failed: $Launcher missing" }
    Ok "sandbox app copy ready: $AppDir (from $srcDir)"
}

function Invoke-Setup {
    Step 'SETUP (idempotent)'
    Assert-HostSafety
    foreach ($d in @($LabHome, $DataDir, $PidDir, $LogsDir, $Stash, $WebviewDir, $NpmCache)) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
    }
    Ensure-NodeExe
    $v = & $NodeExe --version
    Ok "lab node: $NodeExe ($v)"
    Sync-FakeDshSources
    Build-Tarballs
    Write-RegistryConfig
    if ($LabMode -eq 'real') {
        Seed-RealBaseline
        Copy-DshProfileData
    }
    Build-Snapshot
    Copy-AppToLab
    Show-ForeignLaunchers
    $hostOwner = Get-PortOwnerPid $HostPort
    if ($hostOwner -gt 0) {
        $p = Get-ProcInfo $hostOwner
        Ok ("host dsh detected on port {0} (pid={1} {2}) - lab will never touch it" -f $HostPort, $hostOwner, $p.Name)
    }
}

# ------------------------------ registry control ----------------------------

function Ensure-Registry {
    $latestUrl = "http://127.0.0.1:$RegPort/%40deepseek-ai%2Fdsh/latest"
    if (Test-HttpOk $latestUrl 2) {
        try {
            $j = (Invoke-WebRequest -Uri $latestUrl -UseBasicParsing -TimeoutSec 2).Content | ConvertFrom-Json
            Ok ("fake registry already up (latest={0})" -f $j.version)
            return
        } catch { }
    }
    Info "starting fake registry..."
    $err = Join-Path $LogsDir 'registry.err.log'
    # [DETACH] WMI parentage: keeps node out of this script's process tree (see above).
    # No shell redirection here - Win32_Process.Create does not interpret '>' ; the
    # server writes its own err log on fatal errors.
    if (Test-Path $err) { Remove-Item $err -Force -ErrorAction SilentlyContinue }
    # [WMI] Forward slashes ONLY here - Win32_Process.Create mangles backslash
    # paths unpredictably ("E:\x" -> "E:x" => ReturnValue 9 path-not-found).
    $wmi = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = '{0} {1}' -f ($NodeExe -replace '\\', '/'), ($RegServer -replace '\\', '/')
    }
    if ($wmi.ReturnValue -ne 0) { throw "WMI process create failed: $($wmi.ReturnValue)" }
    $p = @{ Id = $wmi.ProcessId }
    [System.IO.File]::WriteAllText($PidRegistry, [string]$p.Id)
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        if (Test-HttpOk $latestUrl 2) { Ok ("fake registry up: pid={0} port={1}" -f $p.Id, $RegPort); return }
        Start-Sleep -Milliseconds 300
    }
    throw "fake registry did not become ready (see $err)"
}

# --------------------------- bad-plugin injection ---------------------------
# Simulates a third-party plugin whose SERVER side mounts cleanly (so the web
# shell serves its client.js like any real bundle) but whose CLIENT factory
# throws "plugin fatal: ..." on load -> page probe hits the bad signature ->
# E2008 with plugin evidence -> safe-mode ask. Everything lives under lab home;
# the host .dsh is only ever read.

$script:BadPluginName = 'evil-plugin'
$script:BadPluginDir  = Join-Path $LabHome ('profiles\web\node_modules\' + $script:BadPluginName)

function Inject-BadPlugin {
    Step 'INJECT bad plugin (lab home only)'
    $webProfile = Join-Path $LabHome 'profiles\web\package.json'
    if (-not (Test-Path $webProfile)) { throw "web profile manifest missing: $webProfile (run setup first)" }
    # 1) evil-plugin package files.
    # Contract (verified against dsh-app-boot source): a BUNDLE declares
    # dsh.bundle.patch -> a cordis patch YAML that inserts config rows; the row's
    # `name` resolves as a bare package from the profile node_modules walk. The
    # SERVER side here is a valid minimal cordis plugin so the web shell serves
    # its ./client like any real bundle; the CLIENT factory then throws on load
    # -> page probe bad signature -> safe-mode path (process stays healthy).
    $lib = Join-Path $script:BadPluginDir 'lib'
    New-Item -ItemType Directory -Force -Path $lib | Out-Null
    $pkg = [ordered]@{
        name    = $script:BadPluginName; version = '0.0.1-evil'; type = 'module'
        main    = 'lib/index.js'
        # ./package.json export is REQUIRED: client-modules resolves
        # require.resolve('<name>/package.json') from the config-tree anchor;
        # without it -> ERR_PACKAGE_PATH_NOT_EXPORTED -> silent skip (no entry).
        exports = [ordered]@{ '.' = './lib/index.js'; './client' = './lib/client.js'; './package.json' = './package.json' }
        dsh     = [ordered]@{
            bundle = [ordered]@{ patch = './cordis.patch.yml' }
            client = [ordered]@{ inject = @(); platform = 'web' }
        }
    }
    [System.IO.File]::WriteAllText((Join-Path $script:BadPluginDir 'package.json'),
        ($pkg | ConvertTo-Json -Depth 6) + "`n")
    [System.IO.File]::WriteAllText((Join-Path $script:BadPluginDir 'cordis.patch.yml'), @"
# Evil bundle patch (sandbox test): mounts one broken-client plugin.
- insert:
    - id: evil
      name: '$($script:BadPluginName)'
"@)
    [System.IO.File]::WriteAllText((Join-Path $lib 'index.js'), @"
const name = "evil";
const inject = [];
function apply(ctx) {}
export { apply, inject, name };
"@)
    [System.IO.File]::WriteAllText((Join-Path $lib 'client.js'), @"
window.__ModuleLoader__.load({
  id: "$($script:BadPluginName)",
  factory: (require) => { throw new Error("plugin fatal: injected broken plugin (sandbox test)"); }
});
"@)
    Ok ("evil plugin written: {0}" -f $script:BadPluginDir)
    # 2) register in web profile bundles (backup original once, keep idempotent)
    $manifest = Get-Content $webProfile -Raw | ConvertFrom-Json
    $bundles = @($manifest.dsh.profile.bundles)
    if ($bundles -contains $script:BadPluginName) {
        Ok "evil plugin already registered in bundles"
        return
    }
    $bak = "$webProfile.lab-bak"
    if (-not (Test-Path $bak)) { Copy-Item $webProfile $bak }
    $manifest.dsh.profile.bundles = @($bundles + $script:BadPluginName)
    [System.IO.File]::WriteAllText($webProfile, ($manifest | ConvertTo-Json -Depth 8) + "`n")
    Ok ("evil plugin registered in bundles: [{0}]" -f (($manifest.dsh.profile.bundles) -join ', '))
}

function Remove-BadPlugin {
    # Injection-only artifacts. profiles/web/node_modules NEVER exists outside
    # injection in this lab (profile sync excludes node_modules), so deleting it
    # wholesale is safe; package.json is restored from backup or host resync.
    $removed = $false
    if (Test-Path $script:BadPluginDir) {
        Remove-Item (Join-Path $LabHome 'profiles\web\node_modules') -Recurse -Force
        Ok "evil plugin node_modules removed"
        $removed = $true
    }
    $webProfile = Join-Path $LabHome 'profiles\web\package.json'
    $bak = "$webProfile.lab-bak"
    if (Test-Path $bak) {
        Move-Item $bak $webProfile -Force
        Ok "web package.json restored from backup"
        $removed = $true
    }
    $safeProfile = Join-Path $LabHome 'profiles\.dsh-safe'
    if (Test-Path $safeProfile) {
        Remove-Item $safeProfile -Recurse -Force
        Ok ".dsh-safe leftover removed"
        $removed = $true
    }
    # [LESSON 2026-08-23] safe-mode.json active=true MUST go together with the
    # .dsh-safe dir: a stale active flag makes the next launcher boot demand a
    # profile that no longer exists -> fail-loud -> readiness timeout (E2002).
    $safeState = Join-Path $DataDir 'safe-mode.json'
    if (Test-Path $safeState) {
        Remove-Item $safeState -Force
        Ok "stale safe-mode-state cleared"
        $removed = $true
    }
    if (-not $removed) { Ok "no bad-plugin artifacts present" }
}

# ------------------------------ state restore -------------------------------

function Test-RuntimeComplete([string]$Dir, [string]$ExpectedVersion) {
    $pkg = Join-Path $Dir 'node_modules\@deepseek-ai\dsh\package.json'
    if (-not (Test-Path $pkg)) { return $false }
    try {
        $j = Get-Content $pkg -Raw | ConvertFrom-Json
        if ($j.version -ne $ExpectedVersion) { return $false }
        $binRel = $null
        if ($j.bin -is [string]) { $binRel = $j.bin }
        elseif ($j.bin.dsh) { $binRel = $j.bin.dsh }
        if (-not $binRel) { return $false }
        $binAbs = Join-Path $Dir ("node_modules\@deepseek-ai\dsh\" + ($binRel -replace '/', '\'))
        return (Test-Path $binAbs)
    } catch { return $false }
}

function Restore-Baseline {
    Step "RESTORE $Baseline (instant, local-only)"
    if (-not (Test-Path $Stash)) { New-Item -ItemType Directory -Force -Path $Stash | Out-Null }
    $buildDir = Join-Path $Staging ("runtime-build-" + $Target)
    $stashDir = Join-Path $Stash ("runtime-build-" + $Target)
    if (Test-Path $buildDir) {
        if (Test-RuntimeComplete $buildDir $Target) {
            if (Test-Path $stashDir) { Remove-Item $stashDir -Recurse -Force }
            Move-Item $buildDir $stashDir
            Ok "staged $Target build parked in stash (reusable without re-download)"
        } else {
            Remove-Item $buildDir -Recurse -Force
            Ok "incomplete staged build deleted"
        }
    }
    if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force; Ok "staging cleared" }
    foreach ($f in @($Pending, $Skipped)) {
        if (Test-Path $f) { Remove-Item $f -Force; Ok ("cleared {0}" -f (Split-Path $f -Leaf)) }
    }
    if (Test-Path $Runtimes) {
        foreach ($d in @(Get-ChildItem $Runtimes -Directory)) {
            if ($d.Name -eq $Baseline) { continue }
            Remove-Item $d.FullName -Recurse -Force
            Ok ("removed runtime: {0}" -f $d.Name)
        }
    }
    $baseDir = Join-Path $Runtimes $Baseline
    if (-not (Test-RuntimeComplete $baseDir $Baseline)) {
        if (Test-Path $baseDir) { Remove-Item $baseDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $baseDir | Out-Null
        robocopy (Get-SnapshotDir) $baseDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        if (-not (Test-RuntimeComplete $baseDir $Baseline)) { throw "failed to restore baseline runtime from snapshot" }
        Ok "$Baseline runtime restored from snapshot (robocopy /MIR)"
    } else {
        Ok "$Baseline runtime already pristine"
    }
    Remove-BadPlugin   # injection artifacts first; host resync below restores pristine manifest
    if ($LabMode -eq 'real') { Copy-DshProfileData }
    Ok "state = $Baseline, pending=none, staging=empty (no network used)"
}

# ------------------------------ launcher start ------------------------------

function Write-LauncherEnvCmd {
    # [LEAK FIX] The agent/tool session carries harness-injected DSH_* vars
    # (observed: DSH_WEB_URL=http://127.0.0.1:3080 -> silently flips the launcher
    # into External mode against the HOST service!). Clear every inherited
    # DSH_* / FAKE_DSH* variable BEFORE applying lab values.
    $lines = @(
        '@echo off',
        'rem AUTO-GENERATED by lab.ps1 - sandbox launcher environment (host-safe)',
        'rem ---- clear any inherited DSH_* / FAKE_DSH* (leak guard) ----'
    )
    foreach ($kv in @(Get-ChildItem env: | Where-Object { $_.Name -like 'DSH_*' -or $_.Name -like 'FAKE_DSH*' })) {
        $lines += ('set "{0}="' -f $kv.Name)
    }
    $lines += @(
        'rem ---- lab values ----',
        'set "DSH_SANDBOX=1"',
        ('set "DSH_HOME={0}"' -f $LabHome),
        ('set "USERPROFILE={0}"' -f $LabHome),
        ('set "DSH_WEB_PORT={0}"' -f $SvcPort),
        ('set "DSH_NPM_REGISTRY=http://127.0.0.1:{0}"' -f $RegPort),
        ('set "DSH_NPM_MIRROR=http://127.0.0.1:{0}"' -f $RegPort),
        ('set "DSH_WEBVIEW2_DATA={0}"' -f $WebviewDir),
        ('set "npm_config_cache={0}"' -f $NpmCache),
        'set "npm_config_update_notifier=false"',
        ('set "PATH={0};%%PATH%%"' -f $NodeDir)
    )
    # Test hook (launcher Program.cs): auto-answer the safe-mode modal so the
    # bad-plugin scenario can run unattended. Only written when requested -
    # never pollute ordinary lab runs.
    if ($SafeModeAutoYes) {
        $lines += 'rem ---- test hook: auto-enter safe mode on boot failure ----'
        $lines += 'set "DSH_TEST_SAFE_MODE_ANSWER=yes"'
    }
    $lines += @(
        ('start "" "{0}"' -f $Launcher),
        'exit /b 0'
    )
    Set-Content -Path $EnvCmd -Value $lines -Encoding ASCII
}

function Start-LauncherAndWait {
    Write-LauncherEnvCmd
    Info ("launching sandbox launcher: {0}" -f $Launcher)
    $before = @(Get-Process DshWeb -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    # [DETACH] Spawn via WMI so the GUI tree is parented to WmiPrvSE, completely
    # outside this script's process tree/job - callers that wait on the whole tree
    # would otherwise hang until the launcher exits.
    $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = 'cmd /d /c "{0}"' -f $EnvCmd
    }
    $deadline = (Get-Date).AddSeconds(15)
    $newPid = 0
    while ((Get-Date) -lt $deadline -and $newPid -eq 0) {
        Start-Sleep -Milliseconds 400
        try {
            $mine = @(Get-Process DshWeb -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -eq $Launcher -and $before -notcontains $_.Id })
            if ($mine.Count -gt 0) { $newPid = $mine[0].Id }
        } catch { }
    }
    if ($newPid -eq 0) { Fail "could not capture launcher PID (check $LogsDir)"; return $false }
    # [IO.File] over Set-Content: Set-Content-written .pid files were observed
    # vanishing in proxied pwsh sessions; raw write persists reliably.
    [System.IO.File]::WriteAllText($PidLauncher, [string]$newPid)
    Ok ("launcher pid={0}" -f $newPid)

    Info ("waiting for service on 127.0.0.1:{0} ..." -f $SvcPort)
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        $v = Get-ServedVersion
        if ($v) {
            Ok ("service ready, served version = {0}" -f $v)
            return $true
        }
        Start-Sleep -Milliseconds 800
    }
    Fail "service not ready within 90s - check log: $DataDir\dsh.log"
    return $false
}

function Move-StashToStaging {
    $stashDir = Join-Path $Stash ("runtime-build-" + $Target)
    if (-not (Test-Path $stashDir)) { Warn "no staged build in stash"; return $false }
    if (-not (Test-RuntimeComplete $stashDir $Target)) { Warn "stashed build incomplete - ignoring"; return $false }
    New-Item -ItemType Directory -Force -Path $Staging | Out-Null
    $buildDir = Join-Path $Staging ("runtime-build-" + $Target)
    if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
    Move-Item $stashDir $buildDir
    $pending = @{
        version    = $Target
        tarball    = $null
        prefetched = $false
        runtimeDir = $buildDir
        at         = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        failCount  = 0
    }
    $tmp = "$Pending.tmp"
    ($pending | ConvertTo-Json) | Set-Content -NoNewline $tmp -Encoding UTF8
    if (Test-Path $Pending) { Remove-Item $Pending -Force }
    Move-Item $tmp $Pending
    Ok "pending restored from stash: next launch applies $Target instantly (no download)"
    return $true
}

# ------------------------------ status --------------------------------------

function Show-Status {
    Step 'LAB STATUS'
    $hostOwner = Get-PortOwnerPid $HostPort
    if ($hostOwner -gt 0) {
        $p = Get-ProcInfo $hostOwner
        Ok ("HOST dsh on port {0}: pid={1} {2} (untouched)" -f $HostPort, $hostOwner, $p.Name)
    } else {
        Ok ("host port {0}: nothing listening" -f $HostPort)
    }
    $latestUrl = "http://127.0.0.1:$RegPort/%40deepseek-ai%2Fdsh/latest"
    if (Test-HttpOk $latestUrl 2) {
        try {
            $j = (Invoke-WebRequest $latestUrl -UseBasicParsing -TimeoutSec 2).Content | ConvertFrom-Json
            Ok ("fake registry: UP, latest={0}" -f $j.version)
        } catch { Warn "fake registry: answering but unparsable" }
    } else { Ok "fake registry: down" }
    $svcPid = Get-PortOwnerPid $SvcPort
    if ($svcPid -gt 0) {
        $v = Get-ServedVersion
        Ok ("fake-dsh service: UP on port {0}, served version={1}, pid={2}" -f $SvcPort, $v, $svcPid)
    } else { Ok "fake-dsh service: down" }
    if (Test-Path $PidLauncher) {
        $pid0 = 0; [void][int]::TryParse((Get-Content $PidLauncher), [ref]$pid0)
        $p = Get-ProcInfo $pid0
        if ($p -and $p.Name -eq 'DshWeb.exe' -and $p.ExecutablePath -eq $Launcher) { Ok ("sandbox launcher: running pid={0}" -f $pid0) }
        else { Ok "sandbox launcher: not running (stale pid file)" }
    } else { Ok "sandbox launcher: not running" }
    $winner = $null
    if (Test-Path $Runtimes) {
        foreach ($d in @(Get-ChildItem $Runtimes -Directory -ErrorAction SilentlyContinue)) {
            $pkg = Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\package.json'
            if (-not (Test-Path $pkg)) { continue }
            try { $v = (Get-Content $pkg -Raw | ConvertFrom-Json).version } catch { continue }
            Ok ("runtime installed: {0} (complete={1})" -f $v, (Test-RuntimeComplete $d.FullName $v))
            if ($null -eq $winner -or (Compare-LabVersion $v $winner) -gt 0) { $winner = $v }
        }
    }
    if ($winner) { Info ("discovery would pick: {0}" -f $winner) } else { Info "discovery would pick: nothing (no complete runtime)" }
    if (Test-Path $Pending) {
        try {
            $j = Get-Content $Pending -Raw | ConvertFrom-Json
            Ok ("pending-update: version={0} runtimeDir={1}" -f $j.version, $j.runtimeDir)
        } catch { Warn "pending-update: unreadable" }
    } else { Ok "pending-update: none" }
    if (Test-Path (Join-Path $Staging ("runtime-build-" + $Target))) { Ok "staging: staged build present" } else { Ok "staging: empty" }
    if (Test-Path (Join-Path $Stash ("runtime-build-" + $Target))) { Ok "stash: target build parked (reusable, no re-download)" } else { Ok "stash: empty" }
}

# ------------------------------ main ----------------------------------------

try {
    switch ($Command) {
        'setup'  { Invoke-Setup }
        'stop'   { Assert-HostSafety; Stop-Lab }
        'reset'  {
            Assert-HostSafety
            if (-not $NoStop) { Stop-Lab }
            Restore-Baseline
        }
        'start'  {
            Assert-HostSafety
            Show-ForeignLaunchers
            if (-not (Test-Path $Launcher)) { Invoke-Setup }
            if (-not (Test-Path $NodeExe)) { Invoke-Setup }
            if (-not (Test-Path (Join-Path $TarballDir ("deepseek-ai-dsh-{0}.tgz" -f $Target)))) { Invoke-Setup }
            if (-not (Test-Path $Launcher)) { throw "sandbox app copy still missing after setup: $Launcher" }
            if ($Reset) { Stop-Lab; Restore-Baseline }
            else { Stop-Launcher; Stop-FakeService }
            Ensure-Registry
            if ($ReuseStash) { [void](Move-StashToStaging) }
            [void](Start-LauncherAndWait)
            Info ("scenario: installed={0}, registry latest={1}" -f $Baseline, $Target)
            if ($SafeModeAutoYes) { Info "safe-mode test hook: DSH_TEST_SAFE_MODE_ANSWER=yes active" }
            Info "next: click the update toast to download (LOCAL registry), then close the"
            Info "     launcher and run launch-resume.cmd to apply; reset-to-rc6.cmd snaps back."
        }
        'inject-badplugin' {
            Assert-HostSafety
            Inject-BadPlugin
        }
        'status' { Assert-HostSafety; Show-ForeignLaunchers; Show-Status }
    }
} catch {
    Fail $_.Exception.Message
    exit 1
}
