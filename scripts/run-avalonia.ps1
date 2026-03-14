param(
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [string]$Runtime
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Runtime)) {
    $currentArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    $Runtime = if ($currentArchitecture -eq "Arm64") { "win-arm64" } else { "win-x64" }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\BabelPlayer.Avalonia\BabelPlayer.Avalonia.csproj"
$exePath = Join-Path $repoRoot "src\BabelPlayer.Avalonia\bin\Debug\net9.0-windows10.0.22621.0\$Runtime\BabelPlayer.Avalonia.exe"

Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        dotnet restore $projectPath
    }

    if (-not $SkipBuild) {
        Get-Process BabelPlayer.Avalonia -ErrorAction SilentlyContinue | Stop-Process -Force
        dotnet build $projectPath -p:RuntimeIdentifier=$Runtime
    }

    if (-not (Test-Path $exePath)) {
        throw "Avalonia executable not found at '$exePath'."
    }

    Start-Process -FilePath $exePath | Out-Null
}
finally {
    Pop-Location
}
