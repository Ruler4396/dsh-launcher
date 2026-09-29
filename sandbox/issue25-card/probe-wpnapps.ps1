param([Parameter(Mandatory)][string]$Exe, [string]$SceneRoot = "$PSScriptRoot", [int]$WaitSeconds = 14)
# 测一件具体的事：网页通知被 WebView2/Chromium 渲染时，到底是哪个进程加载了 wpnapps.dll。
# 宿主 DshWeb.exe 应当为"无"（issue #25 收口的断言）；msedgewebview2.exe 若命中，
# 说明"放行 Notifications 权限"确实把 WPN 调用重新引到了浏览器进程里。
$ErrorActionPreference = 'Stop'
$scene = Resolve-Path $SceneRoot
$home_ = Join-Path $scene 'home-probe'
$wv2   = Join-Path $scene 'wv2-probe'
$reqs  = Join-Path $scene 'staging\probe-requests.log'
Remove-Item $home_, $wv2 -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $reqs -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $home_, $wv2 | Out-Null

$body = @'
<!doctype html><html><head><meta charset="utf-8"></head><body>probe
<script>
Notification.requestPermission().then(function(p){
  fetch('/perm?value=' + p);
  try { new Notification('probe 通知', { body: 'module probe' }); fetch('/constructed?ok=1'); }
  catch (e) { fetch('/constructed?ok=0'); }
});
</script></body></html>
'@
$pageFile = Join-Path $scene 'staging\_probe.html'
Set-Content -Path $pageFile -Value $body -Encoding utf8
$port = 39700 + (Get-Random -Maximum 90)
$job = Start-Job -ScriptBlock {
    param($p, $pagePath, $reqLog)
    $html = [IO.File]::ReadAllBytes($pagePath)
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $p); $l.Start()
    while ($true) {
        try {
            $c = $l.AcceptTcpClient(); $s = $c.GetStream(); $b = New-Object byte[] 4096
            $n = $s.Read($b, 0, $b.Length)
            Add-Content -Path $reqLog -Value (([Text.Encoding]::ASCII.GetString($b, 0, $n) -split "`r`n")[0])
            $h = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($html.Length)`r`nConnection: close`r`n`r`n"
            $s.Write([Text.Encoding]::ASCII.GetBytes($h)); $s.Write($html); $c.Close()
        } catch { Start-Sleep -m 80 }
    }
} -ArgumentList $port, $pageFile, $reqs

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $Exe; $psi.WorkingDirectory = Split-Path $Exe; $psi.UseShellExecute = $false
foreach ($k in 'DSH_WEB_URL','DSH_HOME','DSH_SHELL','DSH_SESSION_ID','DSH_SESSION_JSONL','DSH_TEST_UPDATE_SIGNAL') {
    $psi.EnvironmentVariables.Remove($k) | Out-Null
}
$psi.EnvironmentVariables['DSH_SANDBOX'] = '1'
$psi.EnvironmentVariables['DSH_HOME'] = $home_
$psi.EnvironmentVariables['DSH_WEB_URL'] = "http://127.0.0.1:$port"
$psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = $wv2
$psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
$psi.EnvironmentVariables['DSH_TELEMETRY_DISABLED'] = '1'
$psi.EnvironmentVariables['DSH_E2E'] = '1'

function Scan-Modules([string]$procName) {
    $out = @()
    foreach ($pr in (Get-Process -Name $procName -ErrorAction SilentlyContinue)) {
        try {
            $m = @($pr.Modules | Where-Object { $_.ModuleName -match 'wpnapps' } | ForEach-Object { $_.ModuleName })
            $out += "  pid=$($pr.Id) 模块数=$($pr.Modules.Count) wpnapps=$(if ($m) { $m -join ',' } else { '无' })"
        } catch { $out += "  pid=$($pr.Id) 模块枚举失败: $($_.Exception.Message)" }
    }
    if ($out.Count -eq 0) { return @("  (无 $procName 进程)") }
    return $out
}

$p = [System.Diagnostics.Process]::Start($psi)
Write-Host "pid=$($p.Id) port=$port 等待 $WaitSeconds s"
Start-Sleep -Seconds $WaitSeconds
Write-Host "`n== 宿主 DshWeb.exe ==";  Scan-Modules 'DshWeb'          | ForEach-Object { Write-Host $_ }
Write-Host "== 浏览器 msedgewebview2.exe =="; Scan-Modules 'msedgewebview2' | ForEach-Object { Write-Host $_ }
Write-Host "`n== 页面回报 =="; if (Test-Path $reqs) { Get-Content $reqs | Where-Object { $_ -match 'perm|constructed' } | ForEach-Object { Write-Host $_ } }
try { $p.Kill($true) } catch {}
Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force
