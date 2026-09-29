param([int]$Seconds = 90)
# List every top-level window of the running launcher, with its exact title, so the card's real
# window title is read from the OS instead of guessed. Reuses the proven harness enumerator.
. 'E:\dsh-launcher\sandbox\issue-verify\lib.ps1'
$p = @(Get-Process DshWeb -ErrorAction SilentlyContinue) | Select-Object -First 1
if (-not $p) { 'no launcher running'; exit 1 }
$ctx = @{ Proc = $p }
$deadline = (Get-Date).AddSeconds($Seconds)
$seen = @{}
$shot = 'E:\dsh-launcher\sandbox\issue-verify\shots\card-square-demo.png'
foreach ($w in @()) {}
while ((Get-Date) -lt $deadline) {
  foreach ($cand in @(Q-FindWindows -Ctx $ctx -Title '' -Any)) {
    if (-not $seen.ContainsKey($cand.Title)) {
      $seen[$cand.Title] = $true
      Write-Host ("+ [{0}] {1}x{2} at ({3},{4})" -f $cand.Title, $cand.W, $cand.Ht, $cand.X, $cand.Y)
    }
    if ($cand.Title -eq 'DshNoticeCard' -and -not (Test-Path $shot)) {
      # 卡片刚 Visible 时 WM_PAINT 还没跑，直接抓屏会得到一片未绘制的灰（实测整幅 208,208,208）——
      # 等一下再抓，并且重新取一次矩形。
      Start-Sleep -Milliseconds 1500
      $c2 = @(Q-FindWindows -Ctx $ctx -Title 'DshNoticeCard') | Select-Object -First 1
      if (-not $c2) { Write-Host 'card vanished before the second capture'; continue }
      Add-Type -AssemblyName System.Drawing
      $bmp = New-Object System.Drawing.Bitmap $c2.W, $c2.Ht
      $gr = [System.Drawing.Graphics]::FromImage($bmp)
      $gr.CopyFromScreen($c2.X, $c2.Y, 0, 0, (New-Object System.Drawing.Size($c2.W, $c2.Ht)))
      $gr.Dispose()
      $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
      # 像素复核：左缘整列必须是强调色（红 #D81E06 / 蓝 #2563EB），不得混进边框灰 #D1D5DB 或底色
      $bad = @()
      for ($y = 0; $y -lt $c2.Ht; $y++) {
        $p = $bmp.GetPixel(0, $y)
        if (-not (($p.R -ge 200 -and $p.G -lt 60 -and $p.B -lt 40) -or ($p.B -ge 200 -and $p.R -lt 80))) {
          $bad += "y=$y rgb=$($p.R),$($p.G),$($p.B)"
        }
      }
      Write-Host ("CARD captured -> $shot ; left column $($c2.Ht) rows, off-accent = $($bad.Count)")
      if ($bad.Count -gt 0) { $bad | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" } }
      $bmp.Dispose()
    }
  }
  Start-Sleep -Milliseconds 300
}
Write-Host ("distinct titles seen: " + ($seen.Keys -join ' | '))
