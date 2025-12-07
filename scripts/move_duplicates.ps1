$src='E:\FluxParser\dev'
$ts=Get-Date -Format 'yyyyMMdd_HHmmss'
$backup=Join-Path $src ("duplicates_backup_$ts")
New-Item -Path $backup -ItemType Directory | Out-Null
$files=@('.gitignore','AddonManager.cs','App.axaml','App.axaml.cs','Flux.csproj','FrameManager.cs','LuaRunner.cs','MainWindow.axaml','MainWindow.axaml.cs','Program.cs')
Write-Output "Backup folder: $backup"
foreach ($f in $files) {
    $p = Join-Path $src $f
    if (Test-Path $p) {
        Write-Output "Moving: $p -> $backup"
        Move-Item -LiteralPath $p -Destination $backup -Force
    } else {
        Write-Output "Not found (skipping): $p"
    }
}

Set-Location -LiteralPath (Join-Path $src 'Flux')
git status --porcelain -b
