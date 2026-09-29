$n = 0
foreach ($l in (Get-Content 'E:\dsh-launcher\src\DshShell\Program.cs')) {
    $t = $l.Trim()
    if ($t -eq '') { continue }
    if ($t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('*')) { continue }
    $n++
}
Write-Host "G1 Program.cs code lines = $n (gate <= 2001)"
