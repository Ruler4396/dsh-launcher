# Crop + downscale a region of a full-desktop screenshot so it can be inspected visually.
# Coordinates are PHYSICAL pixels of the captured virtual screen (bitmap origin = VirtualScreen.X/Y,
# which can be negative on a vertically offset second monitor).
param(
    [Parameter(Mandatory)][string]$Src,
    [Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y,
    [Parameter(Mandatory)][int]$W, [Parameter(Mandatory)][int]$H,
    [Parameter(Mandatory)][string]$Out,
    [int]$MaxWidth = 1280,
    [int]$OriginX = 0, [int]$OriginY = 0)
Add-Type -AssemblyName System.Drawing
$src_img = [System.Drawing.Image]::FromFile($Src)
$bx = $X - $OriginX; $by = $Y - $OriginY
if ($bx -lt 0) { $bx = 0 }; if ($by -lt 0) { $by = 0 }
if ($bx + $W -gt $src_img.Width)  { $W = $src_img.Width  - $bx }
if ($by + $H -gt $src_img.Height) { $H = $src_img.Height - $by }
$crop = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($crop)
$g.DrawImage($src_img, (New-Object System.Drawing.Rectangle 0, 0, $W, $H),
                      (New-Object System.Drawing.Rectangle $bx, $by, $W, $H), [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose(); $src_img.Dispose()
$ratio = [Math]::Min(1.0, ($MaxWidth / $W))
$ow = [int]($W * $ratio); $oh = [int]($H * $ratio)
$out_bmp = New-Object System.Drawing.Bitmap $ow, $oh
$g2 = [System.Drawing.Graphics]::FromImage($out_bmp)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g2.DrawImage($crop, 0, 0, $ow, $oh)
$g2.Dispose(); $crop.Dispose()
$out_bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$out_bmp.Dispose()
Write-Host "cropped $W x $H -> $ow x $oh  -> $Out"
