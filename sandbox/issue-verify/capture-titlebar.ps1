param([string]$Title = 'DeepSeek Harness*', [int]$Strip = 40, [string]$Out = 'E:\dsh-launcher\sandbox\issue-verify\shots\titlebar-marker.png')
# Capture a real window's title-bar strip (top $Strip physical pixels) for eyeball + pixel proof.
$ErrorActionPreference = 'Stop'
. 'E:\dsh-launcher\sandbox\issue-verify\lib.ps1'
Add-Type -AssemblyName System.Drawing
$ctx = @{ Proc = (Get-Process DshWeb -ErrorAction SilentlyContinue | Select-Object -First 1) }
# 标题含中文，从 bash 传进来会被 GBK 控制台吃掉——按前缀取最宽的那个窗口（主窗），不传中文。
$w = @(Q-FindWindows -Ctx $ctx -Title $Title) | Sort-Object -Property W -Descending | Select-Object -First 1
if (-not $w) { "window not found: $Title"; exit 1 }
$bmp = New-Object System.Drawing.Bitmap $w.W, $Strip
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($w.X, $w.Y, 0, 0, (New-Object System.Drawing.Size($w.W, $Strip)))
$g.Dispose()
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
# 像素复核：这条带子里必须有"安全模式红"（#D81E06 附近）的像素，且数量不为个位数
$red = 0; $first = ''
for ($y = 0; $y -lt $Strip; $y++) {
  for ($x = 0; $x -lt $w.W; $x++) {
    $p = $bmp.GetPixel($x, $y)
    if ($p.R -ge 190 -and $p.G -le 70 -and $p.B -le 60) {
      $red++
      if (-not $first) { $first = "($x,$y)=$($p.R),$($p.G),$($p.B)" }
    }
  }
}
$bmp.Dispose()
Write-Host ("captured {0}x{1} -> {2}" -f $w.W, $Strip, $Out)
Write-Host ("red pixels in the title strip = {0}  first={1}" -f $red, $first)
