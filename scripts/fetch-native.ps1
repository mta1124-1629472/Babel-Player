# fetch-native.ps1
# Run this once after a fresh clone to download the native libmpv-2.dll binaries.
# These are NOT stored in the repository - they are fetched at build time.
#
# Usage:
#   ./scripts/fetch-native.ps1              # fetches only your current architecture (default)
#   ./scripts/fetch-native.ps1 -All         # fetches both win-x64 and win-arm64
#   ./scripts/fetch-native.ps1 -Runtime win-x64
#   ./scripts/fetch-native.ps1 -Runtime win-arm64

Param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime,

    [Parameter(Mandatory=$false)]
    [switch]$All
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path $PSScriptRoot -Parent

if ($All) {
    Write-Host "Fetching native assets for all runtimes..."
    & "$RepoRoot/build.ps1" FetchNativeAssets
} elseif ($Runtime) {
    $target = if ($Runtime -eq "win-x64") { "FetchNativeX64Asset" } else { "FetchNativeArm64Asset" }
    Write-Host "Fetching native assets for $Runtime..."
    & "$RepoRoot/build.ps1" $target
} else {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    $target = if ($arch -eq "Arm64") { "FetchNativeArm64Asset" } else { "FetchNativeX64Asset" }
    Write-Host "Fetching native assets for current architecture ($arch)..."
    & "$RepoRoot/build.ps1" $target
}

Write-Host ""
Write-Host "Done. You can now build and run the project normally."
