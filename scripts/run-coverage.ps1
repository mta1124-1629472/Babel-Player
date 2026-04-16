# ═══════════════════════════════════════════════════════════════
# run-coverage.ps1 — Build, run tests, and emit OpenCover-style reports
# ═══════════════════════════════════════════════════════════════
#
# Usage:
#   .\scripts\run-coverage.ps1                  # Run tests + coverage
#   .\scripts\run-coverage.ps1 -Filter "Unit" # Extra test filter
#
# Requirements:
#   - .NET 10 SDK installed
#

param(
    [string]$Filter = "",
    [string]$CoverageOutputPath = "TestResults"
)

$ErrorActionPreference = "Stop"

# ── Build ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Building solution" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════" -ForegroundColor Cyan

dotnet build Babel-Player.sln --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# ── Run tests with coverage ────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Running tests with coverage" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════" -ForegroundColor Cyan

$testFilter = "Category!=Integration&Category!=RequiresPython&Category!=RequiresFfmpeg&Category!=RequiresExternalTranslation"
if ($Filter) {
    $testFilter = "$testFilter&$Filter"
}

$testArgs = @(
    "test", "BabelPlayer.Tests/BabelPlayer.Tests.csproj",
    "--no-build",
    "--configuration", "Release",
    "--filter", $testFilter,
    "--results-directory", $CoverageOutputPath,
    "--collect:`"XPlat Code Coverage`"",
    "--",
    "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover",
    "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Output=coverage.xml"
)

& dotnet $testArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "Tests failed!" -ForegroundColor Red
    exit 1
}

# ── Find coverage report ───────────────────────────────────────
$coverageFiles = @(Get-ChildItem -Path $CoverageOutputPath -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("coverage.xml", "coverage.opencover.xml") })

if (-not $coverageFiles -or $coverageFiles.Count -eq 0) {
    Write-Host "No coverage report found in $CoverageOutputPath" -ForegroundColor Red
    exit 1
}

$coverageFile = $coverageFiles[0].FullName
Write-Host ""
Write-Host "Coverage report: $coverageFile" -ForegroundColor Green
Write-Host ""
