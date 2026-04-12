# ═══════════════════════════════════════════════════════════════
# run-coverage.ps1 — Generate coverage report and upload to Codacy
# ═══════════════════════════════════════════════════════════════
#
# Usage:
#   .\scripts\run-coverage.ps1                          # Run tests + upload
#   .\scripts\run-coverage.ps1 -Filter "Unit"           # Filter tests
#   .\scripts\run-coverage.ps1 -UploadOnly              # Only upload existing report
#
# Requirements:
#   - Codacy project token set in CODACY_PROJECT_TOKEN env var
#     OR passed via -CodacyToken parameter
#   - .NET 10 SDK installed
#   - curl available (for Codacy uploader)
#

param(
    [string]$Filter = "",
    [string]$CodacyToken = "",
    [switch]$UploadOnly,
    [string]$CoverageOutputPath = "TestResults"
)

$ErrorActionPreference = "Stop"

# ── Validate Codacy token ──────────────────────────────────────
$token = if ($CodacyToken) { $CodacyToken } else { $env:CODACY_PROJECT_TOKEN }
if (-not $token) {
    Write-Host ""
    Write-Host "ERROR: Codacy project token not set." -ForegroundColor Red
    Write-Host "Set it via:" -ForegroundColor Yellow
    Write-Host "  `$env:CODACY_PROJECT_TOKEN = 'your-token-here'" -ForegroundColor Gray
    Write-Host "OR pass it:" -ForegroundColor Yellow
    Write-Host "  .\scripts\run-coverage.ps1 -CodacyToken 'your-token-here'" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Get your token from: https://app.codacy.com/gh/<org>/<repo>/settings/coverage" -ForegroundColor Gray
    exit 1
}

# ── Build ──────────────────────────────────────────────────────
if (-not $UploadOnly) {
    Write-Host ""
    Write-Host "════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host " Building solution" -ForegroundColor Cyan
    Write-Host "════════════════════════════════════════════════" -ForegroundColor Cyan

    dotnet build Babel-Player.sln --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
}

# ── Run tests with coverage ────────────────────────────────────
if (-not $UploadOnly) {
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
}

# ── Find coverage report ───────────────────────────────────────
$coverageDir = Join-Path $CoverageOutputPath "*"
$coverageFiles = Get-ChildItem -Path $coverageDir -Filter "coverage.xml" -Recurse -ErrorAction SilentlyContinue

if (-not $coverageFiles -or $coverageFiles.Count -eq 0) {
    Write-Host "No coverage report found in $CoverageOutputPath" -ForegroundColor Red
    exit 1
}

$coverageFile = $coverageFiles[0].FullName
Write-Host ""
Write-Host "Found coverage report: $coverageFile" -ForegroundColor Green

# ── Upload to Codacy ───────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Uploading to Codacy" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════" -ForegroundColor Cyan

$env:CODACY_PROJECT_TOKEN = $token

# Download and run Codacy Coverage Reporter
$uploaderScript = Join-Path $CoverageOutputPath "codacy-coverage.sh"
try {
    Invoke-WebRequest -Uri "https://coverage.codacy.com/get.sh" -OutFile $uploaderScript -UseBasicParsing
    bash $uploaderScript report --language C# --coverage-reports $coverageFile --partial
} finally {
    if (Test-Path $uploaderScript) {
        Remove-Item $uploaderScript -Force
    }
}

Write-Host ""
Write-Host "Coverage uploaded successfully!" -ForegroundColor Green
Write-Host "View it at: https://app.codacy.com/gh/$($env:GITHUB_REPOSITORY)/coverage" -ForegroundColor Gray
Write-Host ""
