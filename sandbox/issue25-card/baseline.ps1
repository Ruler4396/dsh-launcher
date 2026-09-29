param(
    [Parameter(Mandatory)][ValidateSet('default','forcetoast','safemode','webnotify','dedupe')][string]$Scenario,
    [Parameter(Mandatory)][string]$Exe,
    [string]$SceneRoot = "$PSScriptRoot",
    [int]$WaitSeconds = 12,
    [string]$Label = "new"
)
# 通知体验基线/对照夹具：同一套场景分别跑"修改前"和"修改后"的 DshWeb.exe，
# 产出整屏截图 + dsh.log 通知相关行 + 网页通知权限回报，供人工对照。
# 隔离纪律同 docs/sandbox-notes/issue25-wpnapps.md：DSH_SANDBOX=1 + 独立 DSH_HOME/
# WebView2 数据 + 外部托管假服务（绝不拉起真实 dsh，绝不碰宿主 3080）。
$ErrorActionPreference = 'Stop'
$scene = Resolve-Path $SceneRoot
$home_ = Join-Path $scene ("home-" + $Scenario)
$wv2   = Join-Path $scene ("wv2-"   + $Scenario)
$shot  = Join-Path $scene ("staging\" + $Scenario + "-" + $Label + ".png")
$reqs  = Join-Path $scene ("staging\" + $Scenario + "-requests.log")
Remove-Item $home_, $wv2 -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $shot, $reqs -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $home_, $wv2 | Out-Null

$port = 39860 + (Get-Random -Maximum 90)
$body = @'
<!doctype html><html><head><meta charset="utf-8"></head><body style="font:16px sans-serif">
issue25 通知基线页
<script>
function ping(s){ try { fetch('/' + s); } catch(e){} }
var before = (typeof Notification === 'undefined') ? 'no-api' : Notification.permission;
ping('perm-before?value=' + before);
if (typeof Notification !== 'undefined') {
  Notification.requestPermission().then(function(p){
    ping('perm-asked?value=' + p + '&after=' + Notification.permission);
    try { new Notification('dsh 插件通知测试', { body: '来自网页 Notification API' });
          ping('notify-constructed?ok=1&perm=' + Notification.permission); }
    catch (e) { ping('notify-constructed?ok=0&err=' + encodeURIComponent(String(e).slice(0,80))); }
  }, function(r){ ping('perm-rejected?value=' + r); });
}
</script></body></html>
'@

$pageFile = Join-Path $scene 'staging\_page.html'
Set-Content -Path $pageFile -Value $body -Encoding utf8
$job = Start-Job -ScriptBlock {
    param($p, $bodyPath, $reqLog)
    $html = [IO.File]::ReadAllBytes($bodyPath)
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $p); $l.Start()
    while ($true) {
        try {
            $c = $l.AcceptTcpClient(); $s = $c.GetStream()
            $b = New-Object byte[] 8192
            $n = $s.Read($b, 0, $b.Length)
            $head = [Text.Encoding]::ASCII.GetString($b, 0, $n) -split "`r`n"
            $line0 = if ($head.Count -gt 0) { $head[0] } else { '?' }
            Add-Content -Path $reqLog -Value ($line0.Trim())
            $h = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($html.Length)`r`nConnection: close`r`n`r`n"
            $s.Write([Text.Encoding]::ASCII.GetBytes($h)); $s.Write($html); $c.Close()
        } catch { Start-Sleep -m 80 }
    }
} -ArgumentList $port, $pageFile, $reqs

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $Exe
$psi.WorkingDirectory = Split-Path $Exe
$psi.UseShellExecute = $false
foreach ($k in 'DSH_WEB_URL','DSH_HOME','DSH_SHELL','DSH_SESSION_ID','DSH_SESSION_JSONL',
               'DSH_TEST_TOAST','DSH_TEST_NOTICE_CARD','DSH_TEST_FORCE_TOAST','DSH_TEST_FORCE_TOAST_FAIL',
               'DSH_ENABLE_SYSTEM_TOAST','DSH_TEST_UPDATE_SIGNAL','DSH_TEST_INSTALL_MODE') {
    $psi.EnvironmentVariables.Remove($k) | Out-Null
}
$psi.EnvironmentVariables['DSH_SANDBOX'] = '1'
$psi.EnvironmentVariables['DSH_HOME'] = $home_
$psi.EnvironmentVariables['DSH_WEB_URL'] = "http://127.0.0.1:$port"
$psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = $wv2
$psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
$psi.EnvironmentVariables['DSH_TELEMETRY_DISABLED'] = '1'
$psi.EnvironmentVariables['DSH_TEST_INSTALL_MODE'] = 'msi'   # MSI 形态才有系统通知通路

switch ($Scenario) {
    'default'   { $psi.EnvironmentVariables['DSH_TEST_TOAST'] = '1'
                  $psi.EnvironmentVariables['DSH_TEST_UPDATE_SIGNAL'] = 'dsh:9.9.9' }
    'forcetoast'{ $psi.EnvironmentVariables['DSH_TEST_TOAST'] = '1'
                  $psi.EnvironmentVariables['DSH_TEST_FORCE_TOAST'] = '1'
                  $psi.EnvironmentVariables['DSH_TEST_UPDATE_SIGNAL'] = 'dsh:9.9.9' }
    'dedupe'    { $psi.EnvironmentVariables['DSH_TEST_NOTICE_CARD'] = '1' }
                  # 自检块内会再连送两条**完全相同**的内容 → 第二条必须被去重闸门吞掉
    'safemode'  { $sm = Join-Path $home_ 'dsh-launcher'
                  New-Item -ItemType Directory -Path $sm | Out-Null
                  Set-Content -Path (Join-Path $sm 'safe-mode.json') -Value '{"active":true,"tier":1}' -Encoding utf8 }
    'webnotify' { }
}

$p = [System.Diagnostics.Process]::Start($psi)
Write-Host "scenario=$Scenario exe=$Exe pid=$($p.Id) port=$port"
Start-Sleep -Seconds $WaitSeconds

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "screenshot -> $shot"

try { $p.Kill($true) } catch {}
Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force

$log = Join-Path $home_ 'dsh-launcher\dsh.log'
Write-Host "`n---- dsh.log 通知相关行 ----"
if (Test-Path $log) {
    Get-Content $log | Where-Object {
        $_ -match 'toast|notice|wpnapps|safe.?mode|permission|balloon|title bar'
    } | ForEach-Object { Write-Host $_ }
} else { Write-Host "(无日志文件)" }
Write-Host "`n---- 网页通知权限回报（假服务收到的请求） ----"
if (Test-Path $reqs) { Get-Content $reqs | Where-Object { $_ -match 'perm|notify' } | ForEach-Object { Write-Host $_ } }
else { Write-Host "(无请求日志)" }
Write-Host "`n---- 进程模块里是否有 wpnapps.dll（重新起一次并扫描） ----"
$psi2 = $psi; $p2 = [System.Diagnostics.Process]::Start($psi2)
Start-Sleep -Seconds 9
try {
    $hits = $p2.Modules | Where-Object { $_.ModuleName -match 'wpnapps' } | ForEach-Object { $_.ModuleName }
    Write-Host ("wpnapps 命中: " + $(if ($hits) { $hits -join ',' } else { '无' }))
} catch { Write-Host "模块枚举失败: $($_.Exception.Message)" }
try { $p2.Kill($true) } catch {}
Write-Host "`nhome=$home_"
