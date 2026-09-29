$ErrorActionPreference = 'Stop'
$root  = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$scene = Split-Path $PSScriptRoot -Parent
$exe   = Join-Path $root 'src\DshShell\bin\Debug\net10.0-windows\DshWeb.exe'
$port  = 39879

$job = Start-Job -ScriptBlock {
  param($p)
  $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $p); $l.Start()
  $body = [Text.Encoding]::UTF8.GetBytes('<!doctype html><html><body>x</body></html>')
  for ($i = 0; $i -lt 300; $i++) {
    try {
      $c = $l.AcceptTcpClient(); $s = $c.GetStream(); $b = New-Object byte[] 4096
      [void]$s.Read($b, 0, $b.Length)
      $h = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
      $s.Write([Text.Encoding]::ASCII.GetBytes($h)); $s.Write($body); $c.Close()
    } catch { Start-Sleep -m 50 }
  }
  $l.Stop()
} -ArgumentList $port

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $exe
$psi.WorkingDirectory = Split-Path $exe
$psi.UseShellExecute = $false
foreach ($k in 'DSH_WEB_URL','DSH_HOME','DSH_SHELL','DSH_SESSION_ID','DSH_SESSION_JSONL',
               'DSH_TEST_NOTICE_CARD','DSH_TEST_TOAST','DSH_ENABLE_SYSTEM_TOAST') {
  $psi.EnvironmentVariables.Remove($k) | Out-Null
}
$psi.EnvironmentVariables['DSH_SANDBOX'] = '1'
$psi.EnvironmentVariables['DSH_HOME'] = (Join-Path $scene 'home')
$psi.EnvironmentVariables['DSH_WEB_URL'] = "http://127.0.0.1:$port"
$psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = (Join-Path $scene 'webview2')
$psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
$psi.EnvironmentVariables['DSH_TEST_INSTALL_MODE'] = 'msi'
# 假更新信号：下游与真实信号同一条 NotifyPending 分支（标题 + 两行正文 + 点击动作）
$psi.EnvironmentVariables['DSH_TEST_UPDATE_SIGNAL'] = 'launcher:9.9.9'
$psi.EnvironmentVariables['DSH_TELEMETRY_DISABLED'] = '1'

$p = [System.Diagnostics.Process]::Start($psi)
Write-Host "pid=$($p.Id)"
Start-Sleep -Seconds 18

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$wa = [System.Windows.Forms.SystemInformation]::WorkingArea
$w = [Math]::Min(760, $wa.Width); $h = [Math]::Min(430, $wa.Height)
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($wa.Right - $w, $wa.Bottom - $h, 0, 0, [System.Drawing.Size]::new($w, $h))
$png = Join-Path $scene 'staging\card-action.png'
$bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved $png"
try { $p.Kill($true) } catch {}
Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force
