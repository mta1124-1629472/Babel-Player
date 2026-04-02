# Babel Player for Windows

This GitHub release ships a portable `win-x64` build of Babel Player.

## What is included

- `BabelPlayer.exe`
- the .NET runtime needed to run the app
- `libmpv-2.dll`
- `ffmpeg.exe`

You do **not** need to install the .NET runtime separately for this release build.

## What you still need

- Windows 10 or 11 x64
- Python 3.10 or newer on `PATH`
- any model/runtime prerequisites for the providers you choose inside the app

Examples:

- `faster-whisper` requires Python packages and local model downloads
- `nllb-200` requires Python packages and local model downloads
- `piper` requires Python packages and a Piper voice model
- `containerized` requires a reachable inference service URL

## Install

1. Download `Babel-Player-<version>-win-x64-portable.zip`
2. Download `Babel-Player-<version>-win-x64-portable.sha256`
3. Extract the zip to a normal folder such as `C:\Apps\BabelPlayer`
4. Run `BabelPlayer.exe`

## First-run notes

- Session data lives under `%LOCALAPPDATA%\BabelPlayer`
- The app bundles `ffmpeg.exe` and `libmpv-2.dll`
- Provider configuration, API keys, and model downloads are still your responsibility
- If you use the containerized provider, `INFERENCE_SERVICE_URL` overrides the saved service URL at startup

## Current limits

- Portable zip only; no installer yet
- Windows only
- No export pipeline yet
- Python is not bundled
