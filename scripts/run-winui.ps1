param(
    [switch]$SkipRestore,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot "BabelPlayer.sln"
$projectPath = Join-Path $repoRoot "src\\BabelPlayer.WinUI\\BabelPlayer.WinUI.csproj"
$exePath = Join-Path $repoRoot "src\\BabelPlayer.WinUI\\bin\\x64\\Debug\\net8.0-windows10.0.22621.0\\BabelPlayer.WinUI.exe"

Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        dotnet restore $solutionPath
    }

    if (-not $SkipBuild) {
        Get-Process BabelPlayer.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
        dotnet build $projectPath -p:Platform=x64
    }

    if (-not (Test-Path $exePath)) {
        throw "WinUI executable not found at '$exePath'."
    }

    Start-Process -FilePath $exePath | Out-Null
}
finally {
    Pop-Location
}
