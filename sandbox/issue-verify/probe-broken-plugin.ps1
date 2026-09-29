# 探针实验（不是验收场景）：往真 dsh 的 web profile 里塞一个"加载即抛"的第三方插件，
# 看壳拿到的是哪一路证据（E2007 进程先退 / 日志层插件签名 / 还是 dsh 自己吞掉了）。
# 这一步决定"坏插件 → 安全模式"能不能全程用真实触发来演示。
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'lib.ps1')
$c = Q-NewScene -Name 'T13'
Q-Start $c | Out-Null
try {
    $r = Q-WaitLogPid $c @('HEALTHY', 'service readiness failed', 'E2002') 200
    Write-Host "  baseline service: $($r.hit)"
} finally { Q-Stop $c }
if (-not (Test-Path (Join-Path $c.Home 'profiles/web/package.json'))) {
    Write-Host '  no web profile created by dsh; cannot continue'; exit 1 }

# 坏插件：入口在 require 时抛，且 package.json 声明自己是 dsh bundle
$plug = Join-Path $c.Home 'profiles/web/node_modules/dsh-broken-demo'
New-Item -ItemType Directory -Force -Path $plug | Out-Null
Set-Content (Join-Path $plug 'package.json') '{"name":"dsh-broken-demo","version":"0.0.1","main":"index.js","dsh":{"bundle":true}}'
Set-Content (Join-Path $plug 'index.js') "throw new Error('verify-T13 broken plugin: boom at load time')"
$pkgPath = Join-Path $c.Home 'profiles/web/package.json'
$j = Get-Content $pkgPath -Raw | ConvertFrom-Json
$j.dependencies | Add-Member -NotePropertyName 'dsh-broken-demo' -NotePropertyValue 'file:./node_modules/dsh-broken-demo' -Force
$j.dsh.profile.bundles = @($j.dsh.profile.bundles) + 'dsh-broken-demo'
$j | ConvertTo-Json -Depth 8 | Set-Content $pkgPath -Encoding utf8
Write-Host "  profile now declares: $((Get-Content $pkgPath -Raw) -replace '\s+',' ')"

$c2 = Q-NewScene -Name 'T13' -Reuse
Q-Start $c2 | Out-Null
try {
    $r2 = Q-WaitLogPid $c2 @('HEALTHY', 'service readiness failed', 'E2002', 'E2007', 'E2001',
                             'plugin', 'PluginCrash', 'E1008') 200
    Write-Host "  with broken plugin: hit=$($r2.hit)"
    Write-Host "  line: $($r2.line.Substring(0, [Math]::Min(220, $r2.line.Length)))"
    Q-Lines $c2 'E200|E1008|plugin|Plugin|readiness|exited|HEALTHY' 14
    $w = Q-WaitWindow $c2 'DeepSeek Harness*' 20
    if ($w) { Write-Host ("  main window present: [{0}]" -f $w.Title) }
    Q-Shot $c2 'T13-0-probe' | Out-Null
} finally { Q-Stop $c2 }
