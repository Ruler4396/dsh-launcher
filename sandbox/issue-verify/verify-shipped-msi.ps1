param([string]$Tag = 'v0.5.1')
$ErrorActionPreference = 'Stop'
$dir = "E:\dsh-launcher\sandbox\issue-verify\msi-$Tag"
$ver = $Tag.TrimStart('v')
$mSi = Join-Path $dir "dsh-launcher-$ver.msi"
$extract = Join-Path $dir 'extracted'
New-Item -ItemType Directory -Force -Path $extract | Out-Null

Write-Host "=== 下载发布产物 $Tag 的 MSI ==="
gh release download $Tag --repo Ruler4396/dsh-launcher --pattern "*.msi" --dir $dir --clobber
$hash = Get-FileHash $mSi -Algorithm SHA256
Write-Host "  file=$mSi"
Write-Host "  sha256=$($hash.Hash.ToLower())"
$sums = Get-Content (Join-Path $dir 'SHA256SUMS.txt') -ErrorAction SilentlyContinue
if (-not $sums) {
    gh release download $Tag --repo Ruler4396/dsh-launcher --pattern "SHA256SUMS.txt" --dir $dir --clobber
    $sums = Get-Content (Join-Path $dir 'SHA256SUMS.txt')
}
$expected = ($sums | Where-Object { $_ -match [regex]::Escape("dsh-launcher-$ver.msi") }) -replace '\s.*$',''
Write-Host "  SHA256SUMS 里登记的=$expected 实际=$($hash.Hash.ToLower()) 一致=$($expected -eq $hash.Hash.ToLower())"

Write-Host '=== 管理员抽取（msiexec /a，只解文件，不装、不写注册表）==='
$p = Start-Process msiexec.exe -ArgumentList @('/a', "`"$mSi`"", '/qn', "TARGETDIR=`"$extract`"") -Wait -PassThru
Write-Host "  msiexec exit=$($p.ExitCode)"
$found = @(Get-ChildItem $extract -Recurse -Filter 'PrereqCheck.exe' -ErrorAction SilentlyContinue)
if ($found.Count -eq 0) { throw 'MSI 里没有 PrereqCheck.exe' }
$exe = $found[0].FullName
Write-Host "  抽出：$exe"

Write-Host '=== 用持久（注册表）PATH 跑产物里的 PrereqCheck --selftest-node ==='
$out = Join-Path $dir 'selftest.txt'
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = "`"--selftest-node`" `"$out`""
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables['PATH'] =
    [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
    [Environment]::GetEnvironmentVariable('PATH', 'User')
$pr = [System.Diagnostics.Process]::Start($psi)
$pr.WaitForExit()
Write-Host "  exit=$($pr.ExitCode)  (0=判定有 node，放行安装)"
Get-Content $out | ForEach-Object { Write-Host "    $_" }
