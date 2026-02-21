# Minimal model download helper (placeholder). Replace URLs and checksums before use.
param(
  [string]$manifestPath = ".\src\PlayerApp.Models\model_manifest.json",
  [string]$dest = "$env:LOCALAPPDATA\PlayerApp\models"
)

Write-Host "Reading manifest: $manifestPath"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

foreach ($m in $manifest.models) {
  $dir = Join-Path $dest $m.type
  if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  $target = Join-Path $dir $m.file_name
  if (-not (Test-Path $target)) {
    Write-Host "Model $($m.display_name) not found. Please download manually or add URL to script."
    # Example: Invoke-WebRequest -Uri $m.download_url -OutFile $target
  } else {
    Write-Host "Model $($m.display_name) already present."
  }
}
