# Babel Player for Windows

## Download

| Option | File | Best for |
|---|---|---|
| Installer | `Babel-Player-<version>-win-x64-setup.exe` | Most users |
| Portable ZIP (x64) | `Babel-Player-<version>-win-x64-portable.zip` | No-install environments |
| Portable ZIP (ARM64) | `Babel-Player-<version>-win-arm64-portable.zip` | Windows on ARM |

Optional checksum verification:

```powershell
Get-FileHash .\Babel-Player-*-setup.exe -Algorithm SHA256
```

## What Is Included

- `BabelPlayer.exe`
- bundled .NET runtime
- `libmpv-2.dll`
- `ffmpeg.exe`
- bundled Windows tooling such as `uv.exe`
- Python host assets under `inference/`

## System Requirements

- Windows 10 or 11
- `x64` or `ARM64`
- NVIDIA CUDA-capable GPU only if you want local GPU transcription, translation, Qwen TTS, or NeMo diarization

CPU-only use is still supported for Faster Whisper, CTranslate2, Piper, and WeSpeaker.

## First-Use Downloads

The app manages Python environments automatically. No separate Python install is required.

Depending on what you select, first use may download:

- Faster Whisper models
- NLLB or Parakeet models
- Qwen3-TTS model weights
- Piper voices
- managed CPU or GPU runtime dependencies

These downloads are stored under `%LOCALAPPDATA%\BabelPlayer\runtime\`.

## Install

### Installer

1. Run `Babel-Player-<version>-win-x64-setup.exe`.
2. Follow the wizard.
3. Launch Babel Player from Start Menu.

### Portable

1. Extract the ZIP to a folder of your choice.
2. Run `BabelPlayer.exe`.

## Notes

- App data lives under `%LOCALAPPDATA%\BabelPlayer`.
- Sessions and generated artifacts live under `%LOCALAPPDATA%\BabelPlayer\sessions\`.
- The managed local GPU host is the default GPU backend.
- `INFERENCE_SERVICE_URL` can override the saved Docker-host GPU URL when the advanced Docker backend is selected.
- Export is available for captions, dubbed audio, and muxed video.
