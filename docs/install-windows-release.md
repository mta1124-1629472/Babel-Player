# Babel Player for Windows

## Download

| Option | File | Best for |
|---|---|---|
| **Installer** (recommended) | `Babel-Player-<version>-win-x64-setup.exe` | Most users — adds Start Menu shortcut + uninstaller |
| **Portable ZIP** | `Babel-Player-<version>-win-x64-portable.zip` | USB drives, no-install environments |

Verify your download (optional):
```powershell
Get-FileHash .\Babel-Player-*-setup.exe -Algorithm SHA256
```
Compare against the matching `.sha256` file.

---

## What's Included

- `BabelPlayer.exe` — the app
- .NET runtime — bundled, no separate install needed
- `libmpv-2.dll` — video playback
- `ffmpeg.exe` — audio extraction and processing

---

## System Requirements

- Windows 10 or 11, 64-bit
- GPU optional but recommended for transcription/TTS

---

## AI Provider Prerequisites

The app manages Python and a local venv automatically (no separate installation needed). You are only responsible for any model downloads the first time you use a provider.

| Provider | What gets downloaded on first use |
|---|---|
| `faster-whisper` | Whisper model weights (~150 MB – 3 GB depending on model size) |
| `nllb-200` | NLLB translation model (~2.5 GB) |
| `xtts-v2` | Coqui XTTS v2 voice model (~2 GB) |
| `piper` | Piper voice model of your choice |
| `containerized` | Nothing — points to an external inference service URL |

---

## First Run

### Installer
1. Run `Babel-Player-<version>-win-x64-setup.exe`
2. Follow the wizard — installs to `%LocalAppData%\Programs\BabelPlayer` by default (no admin required)
3. Launch from the Start Menu shortcut

### Portable ZIP
1. Download and extract to any folder (e.g. `C:\Apps\BabelPlayer`)
2. Run `BabelPlayer.exe`

---

## Notes

- App data and settings live under `%LOCALAPPDATA%\BabelPlayer`
- The Python venv is created automatically on first provider use under `%LOCALAPPDATA%\BabelPlayer\runtime\venv`
- If using the containerized provider, set `INFERENCE_SERVICE_URL` as an environment variable to override the saved URL at startup
- To uninstall the installer version: **Settings → Apps** or the Start Menu uninstall shortcut

---

## Known Limitations

- Windows only (Linux/macOS not yet supported)
- Export pipeline not yet available