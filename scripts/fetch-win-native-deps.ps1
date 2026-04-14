#requires -Version 5.1
<#
.SYNOPSIS
  Download Windows-native binaries that are too large for Git (and previously used Git LFS).

.DESCRIPTION
  - libmpv-2.dll: pinned zhongfly/mpv-winbuild libmpv dev archive (GitHub Releases)
  - uv.exe: latest astral-sh/uv Windows x86_64 zip
  - ffmpeg.exe: optional; pinned GyanD codexffmpeg build (same source as release workflow)

 Run from repo root or any directory; paths are resolved relative to this script.
#>
param(
    [switch] $IncludeFfmpeg,
    [string] $FfmpegVersion = $env:FFMPEG_VERSION
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$NativeDir = Join-Path $RepoRoot "native/win-x64"
$ToolsDir = Join-Path $RepoRoot "tools/win-x64"

New-Item -ItemType Directory -Force -Path $NativeDir | Out-Null
New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null

# Portable 7-Zip CLI (extracts .7z libmpv archives on clean Windows runners).
$SevenZipRemote = "https://www.7-zip.org/a/7zr.exe"
# Pinned libmpv dev package (contains libmpv-2.dll). Update only after validation.
$LibmpvDevArchiveUrl = "https://github.com/zhongfly/mpv-winbuild/releases/download/2026-04-13-da4789c/mpv-dev-x86_64-v3-20260413-git-da4789c.7z"
$UvZipUrl = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip"

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("babel-fetch-" + [Guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

try {
    $sevenZip = Join-Path $scratch "7zr.exe"
    Write-Host "Downloading 7zr from $SevenZipRemote"
    Invoke-WebRequest -Uri $SevenZipRemote -OutFile $sevenZip

    $libmpvArc = Join-Path $scratch "mpv-dev.7z"
    Write-Host "Downloading libmpv dev archive from $LibmpvDevArchiveUrl"
    Invoke-WebRequest -Uri $LibmpvDevArchiveUrl -OutFile $libmpvArc

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
    Invoke-WebRequest -Uri $UvZipUrl -OutFile $uvZip
    $uvTemp = Join-Path $scratch "uv_temp"
    Expand-Archive -Path $uvZip -DestinationPath $uvTemp -Force
    $uvExe = Get-ChildItem -Path $uvTemp -Filter "uv.exe" -Recurse | Select-Object -First 1
    if (-not $uvExe) {
        throw "Could not find uv.exe in uv archive"
    }
    Move-Item -Path $uvExe.FullName -Destination (Join-Path $ToolsDir "uv.exe") -Force
    Write-Host "Wrote $(Join-Path $ToolsDir 'uv.exe')"

    if ($IncludeFfmpeg) {
        if ([string]::IsNullOrWhiteSpace($FfmpegVersion)) {
            throw "IncludeFfmpeg was set but FfmpegVersion / env:FFMPEG_VERSION is empty."
        }
        $ffmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$FfmpegVersion/ffmpeg-$FfmpegVersion-essentials_build.zip"
        Write-Host "Downloading FFmpeg $FfmpegVersion from $ffmpegUrl"
        $ffmpegZip = Join-Path $scratch "ffmpeg.zip"
        Invoke-WebRequest -Uri $ffmpegUrl -OutFile $ffmpegZip
        $ffmpegTemp = Join-Path $scratch "ffmpeg_temp"
        Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegTemp -Force
        $ffmpegExe = Get-ChildItem -Path $ffmpegTemp -Filter "ffmpeg.exe" -Recurse | Select-Object -First 1
        if (-not $ffmpegExe) {
            throw "Could not find ffmpeg.exe in FFmpeg archive"
        }
        Move-Item -Path $ffmpegExe.FullName -Destination (Join-Path $ToolsDir "ffmpeg.exe") -Force
        Write-Host "Wrote $(Join-Path $ToolsDir 'ffmpeg.exe')"
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
