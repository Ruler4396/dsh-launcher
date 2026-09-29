param([ValidateSet('arm','unarm')][string]$Action = 'arm')
# Reset the REAL user home to a known starting point for the safe-mode demo, with the previous
# state copied into the sandbox first. ASCII-only source; run under pwsh 7.
$ErrorActionPreference = 'Stop'
$home_ = Join-Path $env:USERPROFILE '.dsh'
$flag = Join-Path $home_ 'dsh-launcher\safe-mode.json'
$sandbox = 'E:\dsh-launcher\sandbox\issue-verify'
if (Test-Path $flag) {
  Copy-Item $flag (Join-Path $sandbox ("real-safe-mode." + $Action + ".json")) -Force
  "flag copied to sandbox: real-safe-mode.$Action.json"
}
if ($Action -eq 'unarm') {
  Remove-Item $flag -Force -ErrorAction SilentlyContinue
  "safe-mode flag removed; SafeModeState treats a missing file as inactive"
} else {
  "no change (arm is done by the product itself when the user answers the dialog)"
}
$pkg = Join-Path $home_ 'profiles\web\package.json'
$j = Get-Content $pkg -Raw | ConvertFrom-Json
"bundles now: " + (($j.dsh.profile.bundles) -join ',')
"broken demo bundle present: " + (@($j.dsh.profile.bundles) -contains 'dsh-broken-demo')
"DshWeb running: " + (@(Get-Process DshWeb -ErrorAction SilentlyContinue).Count)
