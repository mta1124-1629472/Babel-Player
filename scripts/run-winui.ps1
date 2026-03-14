param(
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [string]$Runtime
)

Write-Warning "run-winui.ps1 is deprecated. Redirecting to run-avalonia.ps1."
$scriptPath = Join-Path $PSScriptRoot "run-avalonia.ps1"
& $scriptPath -SkipRestore:$SkipRestore -SkipBuild:$SkipBuild -Runtime $Runtime
