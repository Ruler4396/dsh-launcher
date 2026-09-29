$ErrorActionPreference = 'Stop'
# Inject a broken third-party bundle into the REAL user profile, mirroring sandbox scenario T15:
# the bundle is declared in dsh.profile.bundles but its package.json has no `dsh.bundle` field,
# so dsh throws in prepareProfile and exits BEFORE becoming ready -> the shell's pre-readiness
# crash path (defect G). Backup must exist next to this script; ASCII-only source, run under pwsh 7.
$prof = Join-Path $env:USERPROFILE '.dsh\profiles\web'
$pkg = Join-Path $prof 'package.json'
$bak = 'E:\dsh-launcher\sandbox\issue-verify\real-web-package.json.bak'
if (-not (Test-Path $pkg)) { throw "profile manifest not found: $pkg" }
if (-not (Test-Path $bak)) { throw "refusing to touch the real profile without the backup at $bak" }
$hashNow = (Get-FileHash $pkg -Algorithm SHA256).Hash
$hashBak = (Get-FileHash $bak -Algorithm SHA256).Hash
if ($hashNow -ne $hashBak) { throw "backup is not the current file ($hashNow != $hashBak); aborting" }

$plug = Join-Path $prof 'node_modules\dsh-broken-demo'
New-Item -ItemType Directory -Force -Path $plug | Out-Null
Set-Content -Path (Join-Path $plug 'package.json') -Encoding ascii -Force `
    -Value '{"name":"dsh-broken-demo","version":"0.0.1","main":"index.js"}'
Set-Content -Path (Join-Path $plug 'index.js') -Encoding ascii -Force `
    -Value "console.log('broken plugin loaded')"

$j = Get-Content $pkg -Raw | ConvertFrom-Json
$j.dependencies | Add-Member -NotePropertyName 'dsh-broken-demo' `
    -NotePropertyValue 'file:./node_modules/dsh-broken-demo' -Force
$j.dsh.profile.bundles = @($j.dsh.profile.bundles) + 'dsh-broken-demo'
$j | ConvertTo-Json -Depth 10 | Set-Content $pkg -Encoding utf8 -Force

$check = Get-Content $pkg -Raw | ConvertFrom-Json
"injected: bundles=" + (($check.dsh.profile.bundles) -join ',')
"plugin dir: $plug  exists=" + (Test-Path $plug)
"backup kept at: $bak"
