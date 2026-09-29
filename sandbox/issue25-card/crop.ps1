param([string]$InFile, [string]$OutFile, [int]$Left, [int]$Top, [int]$WidthPx, [int]$HeightPx, [int]$ZoomFactor = 2)
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile($InFile)
$rect = New-Object System.Drawing.Rectangle($Left, $Top, $WidthPx, $HeightPx)
$bmp = New-Object System.Drawing.Bitmap($WidthPx, $HeightPx)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dest = New-Object System.Drawing.Rectangle(0, 0, $WidthPx, $HeightPx)
$g.DrawImage($src, $dest, $rect, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose(); $src.Dispose()
$bw = $WidthPx * $ZoomFactor; $bh = $HeightPx * $ZoomFactor
$big = New-Object System.Drawing.Bitmap($bw, $bh)
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.DrawImage($bmp, 0, 0, $bw, $bh)
$g2.Dispose(); $bmp.Dispose()
$big.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()
"saved $OutFile ${bw}x${bh}"
