param(
    [switch]$SkipRestore,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot "BabelPlayer.sln"
$projectPath = Join-Path $repoRoot "src\\BabelPlayer.WinUI\\BabelPlayer.Avalonia.csproj"
$exePath = Join-Path $repoRoot "\src\\BabelPlayer.Avalonia\bin\Debug\net9.0\BabelPlayer.Avalonia.exe"

Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        dotnet restore $solutionPath
    }

    if (-not $SkipBuild) {
        Get-Process BabelPlayer -ErrorAction SilentlyContinue | Stop-Process -Force
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
