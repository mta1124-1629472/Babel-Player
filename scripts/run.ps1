param(
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [string]$Runtime
)

$scriptPath = Join-Path $PSScriptRoot "run-avalonia.ps1"
& $scriptPath -SkipRestore:$SkipRestore -SkipBuild:$SkipBuild -Runtime $Runtime
