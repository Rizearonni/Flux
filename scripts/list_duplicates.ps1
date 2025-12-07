Set-Location -LiteralPath 'e:\FluxParser\dev'
$g = Get-ChildItem -Recurse -File | Group-Object -Property Name | Where-Object { $_.Count -gt 1 }
if ($g) {
    foreach ($i in $g) {
        Write-Output "=== $($i.Name)"
        foreach ($f in $i.Group) { Write-Output $f.FullName }
    }
} else {
    Write-Output 'No duplicate filenames found'
}
