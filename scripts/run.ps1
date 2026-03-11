param(
    [switch]$SkipRestore,
    [switch]$SkipBuild
)

$scriptPath = Join-Path $PSScriptRoot "run-winui.ps1"
& $scriptPath @PSBoundParameters
