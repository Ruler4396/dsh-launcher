$a = [Reflection.Assembly]::LoadFrom('E:\dsh-launcher\src\DshShell\bin\Debug\net10.0-windows\DshWeb.dll')
$t = $a.GetType('DshWeb.Win32.MonitorDpi')
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$m = $t.GetMethod('ForPoint', $flags)
"ForPoint method: $($m -ne $null)"
foreach ($c in @(@(960,540), @(0,0), @(1900,1000))) {
  $p = New-Object Drawing.Point([int]$c[0], [int]$c[1])
  "ForPoint($($c[0]),$($c[1])) = " + $m.Invoke($null, @($p))
}
$g = $a.GetType('DshWeb.ShellLogic+NoticeCardLayout')
$cg = $g.GetMethod('ComputeGeometry')
foreach ($d in 96,89) {
  $r = $cg.Invoke($null, @([int]$d))
  "ComputeGeometry($d) w=" + $r.Width + " textW=" + $r.TextWidth + " pad=" + $r.Padding + " em=" + $r.TitleEmPx + "/" + $r.BodyEmPx
}
