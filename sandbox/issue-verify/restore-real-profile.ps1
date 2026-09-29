$ErrorActionPreference = 'Stop'
# Undo inject-broken-plugin.ps1 on the REAL user profile: restore the backed-up manifest byte for
# byte and delete the fake plugin directory (only that exact path). ASCII-only, run under pwsh 7.
$prof = Join-Path $env:USERPROFILE '.dsh\profiles\web'
$pkg = Join-Path $prof 'package.json'
$bak = 'E:\dsh-launcher\sandbox\issue-verify\real-web-package.json.bak'
$plug = Join-Path $prof 'node_modules\dsh-broken-demo'
if (-not (Test-Path $bak)) { throw "no backup at $bak - refusing to guess a restore" }
Copy-Item $bak $pkg -Force
if (Test-Path $plug) { Remove-Item $plug -Recurse -Force }
$j = Get-Content $pkg -Raw | ConvertFrom-Json
"restored bundles=" + (($j.dsh.profile.bundles) -join ',')
"broken bundle still referenced: " + (@($j.dsh.profile.bundles) -contains 'dsh-broken-demo')
"plugin dir still present: " + (Test-Path $plug)
"manifest sha matches backup: " + ((Get-FileHash $pkg).Hash -eq (Get-FileHash $bak).Hash)
