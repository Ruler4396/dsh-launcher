param([Parameter(Mandatory)][string]$Exe, [string]$SceneRoot = "$PSScriptRoot", [int]$Seconds = 24)
# 决定性测量：网页通知**正在屏幕上显示**的那段时间里，反复扫描全机进程模块，
# 回答"Chromium 渲染网页通知到底走不走 wpnapps.dll、在哪个进程里"。
$ErrorActionPreference = 'Stop'
$scene = Resolve-Path $SceneRoot
$home_ = Join-Path $scene 'home-probe2'; $wv2 = Join-Path $scene 'wv2-probe2'
$reqs  = Join-Path $scene 'staging\probe2-requests.log'
Remove-Item $home_, $wv2 -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $reqs -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $home_, $wv2 | Out-Null

$body = @'
<!doctype html><html><head><meta charset="utf-8"></head><body>probe2
<script>
Notification.requestPermission().then(function(p){
  fetch('/perm?value=' + p);
  var n = 0;
  setInterval(function(){ n++;
    if (n > 4) return;              // 上界：最多 4 条，防失控刷屏（曾经每 2.5s 无限发，30+ 条）
    try { new Notification('probe2 通知 ' + n, { body: 'wpnapps 定位探针' });
          fetch('/fired?n=' + n); } catch (e) { fetch('/fired?err=1'); }
  }, 2500);
});
</script></body></html>
'@
$pageFile = Join-Path $scene 'staging\_probe2.html'; Set-Content -Path $pageFile -Value $body -Encoding utf8
$port = 39500 + (Get-Random -Maximum 90)
$job = Start-Job -ScriptBlock {
    param($p, $pagePath, $reqLog)
    $html = [IO.File]::ReadAllBytes($pagePath)
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $p); $l.Start()
    while ($true) {
        try {
            $c = $l.AcceptTcpClient(); $s = $c.GetStream(); $b = New-Object byte[] 4096
            $n = $s.Read($b, 0, $b.Length)
            Add-Content -Path $reqLog -Value (([Text.Encoding]::ASCII.GetString($b,0,$n) -split "`r`n")[0])
            $h = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($html.Length)`r`nConnection: close`r`n`r`n"
            $s.Write([Text.Encoding]::ASCII.GetBytes($h)); $s.Write($html); $c.Close()
        } catch { Start-Sleep -m 80 }
    }
} -ArgumentList $port, $pageFile, $reqs

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $Exe; $psi.WorkingDirectory = Split-Path $Exe; $psi.UseShellExecute = $false
foreach ($k in 'DSH_WEB_URL','DSH_HOME','DSH_SHELL','DSH_SESSION_ID','DSH_SESSION_JSONL','DSH_TEST_UPDATE_SIGNAL') {
    $psi.EnvironmentVariables.Remove($k) | Out-Null }
$psi.EnvironmentVariables['DSH_SANDBOX'] = '1'
$psi.EnvironmentVariables['DSH_HOME'] = $home_
$psi.EnvironmentVariables['DSH_WEB_URL'] = "http://127.0.0.1:$port"
$psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = $wv2
$psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
$psi.EnvironmentVariables['DSH_TELEMETRY_DISABLED'] = '1'
$psi.EnvironmentVariables['DSH_E2E'] = '1'

$p = [System.Diagnostics.Process]::Start($psi)
Write-Host "pid=$($p.Id) port=$port"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$hits = @{}
$until = (Get-Date).AddSeconds($Seconds)   # 硬墙钟上限：不做全机模块枚举（那是上次失控的真正原因）
while ((Get-Date) -lt $until) {
    Start-Sleep -Seconds 2
    foreach ($pr in (Get-Process -Name 'DshWeb','msedgewebview2' -ErrorAction SilentlyContinue)) {
        try { foreach ($m in $pr.Modules) {
            if ($m.ModuleName -match 'wpnapps') { $hits["$($pr.ProcessName)#$($pr.Id)"] = $m.FileName }
        } } catch {}
    }
}
Write-Host "`n== 扫描期间全机命中 wpnapps 的进程 =="
if ($hits.Count -eq 0) { Write-Host "  无" } else { $hits.GetEnumerator() | ForEach-Object { Write-Host "  $($_.Key)  -> $($_.Value)" } }
Write-Host "`n== 页面回报 =="; if (Test-Path $reqs) { Get-Content $reqs | Where-Object { $_ -match 'perm|fired' } | Select-Object -First 8 | ForEach-Object { Write-Host $_ } }
try { $p.Kill($true) } catch {}
Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force
