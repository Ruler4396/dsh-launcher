# 实时监控 dsh 更新日志
Write-Host "=== 实时监控 dsh 更新日志 ===" -ForegroundColor Cyan
Write-Host "按 Ctrl+C 停止监控" -ForegroundColor Gray
Write-Host ""

$log = "$env:USERPROFILE\.dsh\dsh-launcher\dsh.log"
$lastLine = 0

while ($true) {
    if (Test-Path $log) {
        $lines = Get-Content $log
        if ($lines.Count -gt $lastLine) {
            $newLines = $lines[$lastLine..($lines.Count - 1)]
            foreach ($line in $newLines) {
                if ($line -match "pnpm|build|tarball|staged|pending|atomic|fallback|applied|failed") {
                    Write-Host $line -ForegroundColor Green
                }
            }
            $lastLine = $lines.Count
        }
    }
    Start-Sleep -Seconds 1
}
