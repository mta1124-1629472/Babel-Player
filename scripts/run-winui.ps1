param(
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [string]$Runtime
)

Write-Error "BabelPlayer.WinUI is deactivated and is not a supported runtime path."
Write-Host "Use Avalonia instead:"
Write-Host "  dotnet run --project src/BabelPlayer.Avalonia"
exit 1
