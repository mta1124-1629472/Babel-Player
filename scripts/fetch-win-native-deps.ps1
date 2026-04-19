#requires -Version 5.1
<#
.SYNOPSIS
  Download Windows-native binaries that are too large for Git (and previously used Git LFS).

.DESCRIPTION
  - libmpv-2.dll: pinned zhongfly/mpv-winbuild libmpv dev archive (GitHub Releases)
  - uv.exe: astral-sh/uv Windows zip (x86_64 or aarch64)
  - ffmpeg.exe / ffprobe.exe: fetched by default from the same archives as below
    (essentials/LGPL zips ship both). Use -SkipFfmpeg to opt out.

  On ARM64 Windows, artifacts go under native/win-arm64 and tools/win-arm64.
  On x64 Windows, under native/win-x64 and tools/win-x64.

  Override architecture with -Architecture for CI (e.g. fetch ARM64 deps on an AMD64 runner).

 Run from repo root or any directory; paths are resolved relative to this script.
#>
param(
    [ValidateSet("Auto", "X64", "Arm64")]
    [string] $Architecture = "Auto",
    [switch] $SkipFfmpeg,
    [switch] $IncludeFfmpeg,
    [string] $FfmpegVersion = $env:FFMPEG_VERSION
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($SkipFfmpeg -and $IncludeFfmpeg) {
    throw "Use either -SkipFfmpeg or -IncludeFfmpeg, not both."
}

if ($IncludeFfmpeg) {
    Write-Warning "-IncludeFfmpeg is deprecated. FFmpeg and ffprobe are fetched by default; use -SkipFfmpeg to opt out."
}

if (-not $SkipFfmpeg -and [string]::IsNullOrWhiteSpace($FfmpegVersion)) {
    # Keep local build/runtime bootstrap aligned with the currently validated x64
    # codexffmpeg release when CI does not override FFMPEG_VERSION explicitly.
    $FfmpegVersion = "2026-04-09-git-d3d0b7a5ee"
}

function Invoke-FileDownload {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Uri,
        [Parameter(Mandatory = $true)]
        [string] $OutFile,
        [int] $MaxAttempts = 3
    )

    $lastError = $null

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile
            return
        }
        catch {
            $lastError = $_
            Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue

            $curl = Get-Command "curl.exe" -ErrorAction SilentlyContinue
            if ($null -ne $curl) {
                try {
                    & $curl.Source -fL --output $OutFile $Uri | Out-Null
                    if ($LASTEXITCODE -eq 0 -and (Test-Path $OutFile)) {
                        return
                    }

                    throw "curl.exe exited with code $LASTEXITCODE while downloading $Uri"
                }
                catch {
                    $lastError = $_
                    Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
                }
            }

            if ($attempt -eq $MaxAttempts) {
                throw $lastError
            }

            $delaySeconds = [int][Math]::Pow(2, $attempt - 1)
            Write-Warning "Download attempt $attempt for $Uri failed: $($lastError.Exception.Message). Retrying in $delaySeconds second(s)."
            Start-Sleep -Seconds $delaySeconds
        }
    }

    throw $lastError
}

function Get-TargetArchitecture {
    if ($Architecture -ne "Auto") {
        return $Architecture
    }
    $p = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    switch ($p) {
        "Arm64" { return "Arm64" }
        "X64" { return "X64" }
        default {
            throw "Unsupported process architecture for this script: $p (expected Arm64 or X64)."
        }
    }
}

$targetArch = Get-TargetArchitecture
$rid = if ($targetArch -eq "Arm64") { "win-arm64" } else { "win-x64" }

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$NativeDir = Join-Path $RepoRoot "native/$rid"
$ToolsDir = Join-Path $RepoRoot "tools/$rid"

New-Item -ItemType Directory -Force -Path $NativeDir | Out-Null
New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null

# Portable 7-Zip CLI (extracts .7z libmpv archives on clean Windows runners).
$SevenZipRemote = "https://www.7-zip.org/a/7zr.exe"

# Pinned libmpv dev packages (contain libmpv-2.dll). Update only after validation.
$LibmpvDevArchiveUrl = if ($targetArch -eq "Arm64") {
    "https://github.com/zhongfly/mpv-winbuild/releases/download/2026-04-14-da4789c/mpv-dev-aarch64-20260414-git-da4789c.7z"
} else {
    "https://github.com/zhongfly/mpv-winbuild/releases/download/2026-04-13-da4789c/mpv-dev-x86_64-v3-20260413-git-da4789c.7z"
}

$UvZipUrl = if ($targetArch -eq "Arm64") {
    "https://github.com/astral-sh/uv/releases/latest/download/uv-aarch64-pc-windows-msvc.zip"
} else {
    "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip"
}

Write-Host "fetch-win-native-deps: architecture=$targetArch rid=$rid"

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("babel-fetch-" + [Guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

try {
    $sevenZip = Join-Path $scratch "7zr.exe"
    Write-Host "Downloading 7zr from $SevenZipRemote"
    Invoke-FileDownload -Uri $SevenZipRemote -OutFile $sevenZip

    $libmpvArc = Join-Path $scratch "mpv-dev.7z"
    Write-Host "Downloading libmpv dev archive from $LibmpvDevArchiveUrl"
    Invoke-FileDownload -Uri $LibmpvDevArchiveUrl -OutFile $libmpvArc

    Push-Location $scratch
    try {
        & $sevenZip x $libmpvArc "libmpv-2.dll" -y | Out-Host
    }
    finally {
        Pop-Location
    }

    $extractedDll = Join-Path $scratch "libmpv-2.dll"
    if (-not (Test-Path $extractedDll)) {
        throw "libmpv-2.dll not found after extracting $libmpvArc"
    }
    Move-Item -Path $extractedDll -Destination (Join-Path $NativeDir "libmpv-2.dll") -Force
    Write-Host "Wrote $(Join-Path $NativeDir 'libmpv-2.dll')"

    Write-Host "Downloading uv from $UvZipUrl"
    $uvZip = Join-Path $scratch "uv.zip"
    Invoke-FileDownload -Uri $UvZipUrl -OutFile $uvZip
    $uvTemp = Join-Path $scratch "uv_temp"
    Expand-Archive -Path $uvZip -DestinationPath $uvTemp -Force
    $uvExe = Get-ChildItem -Path $uvTemp -Filter "uv.exe" -Recurse | Select-Object -First 1
    if (-not $uvExe) {
        throw "Could not find uv.exe in uv archive"
    }
    Move-Item -Path $uvExe.FullName -Destination (Join-Path $ToolsDir "uv.exe") -Force
    Write-Host "Wrote $(Join-Path $ToolsDir 'uv.exe')"

    if (-not $SkipFfmpeg) {
        if ($targetArch -eq "Arm64") {
            # GyanD essentials zips are x64-only; BtbN publishes winarm64 LGPL builds (moving "latest" — pin if CI must be byte-stable).
            $ffmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-winarm64-lgpl.zip"
            Write-Host "Downloading FFmpeg (winarm64 LGPL) from $ffmpegUrl"
        } else {
            $ffmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$FfmpegVersion/ffmpeg-$FfmpegVersion-essentials_build.zip"
            Write-Host "Downloading FFmpeg $FfmpegVersion from $ffmpegUrl"
        }
        $ffmpegZip = Join-Path $scratch "ffmpeg.zip"
        Invoke-FileDownload -Uri $ffmpegUrl -OutFile $ffmpegZip
        $ffmpegTemp = Join-Path $scratch "ffmpeg_temp"
        Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegTemp -Force
        $ffmpegExe = Get-ChildItem -Path $ffmpegTemp -Filter "ffmpeg.exe" -Recurse | Select-Object -First 1
        if (-not $ffmpegExe) {
            throw "Could not find ffmpeg.exe in FFmpeg archive"
        }
        Move-Item -Path $ffmpegExe.FullName -Destination (Join-Path $ToolsDir "ffmpeg.exe") -Force
        Write-Host "Wrote $(Join-Path $ToolsDir 'ffmpeg.exe')"

        $ffprobeExe = Get-ChildItem -Path $ffmpegTemp -Filter "ffprobe.exe" -Recurse | Select-Object -First 1
        if (-not $ffprobeExe) {
            throw "Could not find ffprobe.exe in FFmpeg archive (expected next to ffmpeg in GyanD/BtbN builds)"
        }
        Move-Item -Path $ffprobeExe.FullName -Destination (Join-Path $ToolsDir "ffprobe.exe") -Force
        Write-Host "Wrote $(Join-Path $ToolsDir 'ffprobe.exe')"
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
