# Install And Run

## Requirements

1. Windows with media playback support.
2. `.NET 8 SDK`.
3. Visual Studio 2022 or later is recommended if you want to run and debug from the IDE.

## Build

From the repository root:

```powershell
dotnet build PlayerApp.sln
```

## Run

```powershell
dotnet run --project src/PlayerApp.UI
```

If `PlayerApp.UI` is already running, close it before rebuilding or rerunning.

## First-Run Workflow

1. Open a local video with the bottom `Open` button.
2. If a matching `.srt` exists next to the video, the app loads it automatically.
3. If no `.srt` exists, the app generates subtitles from the audio.
4. Use `Subtitles > Transcription Model` to choose local or cloud subtitle generation.
5. Use `Translation` to enable translation for the current video or automatically for non-English videos.
6. If you choose a cloud model, enter an OpenAI API key when prompted.
7. Use `Export EN SRT` to save the English subtitle track.

## Current Model Options

### Subtitle / transcription models

- `Local: Tiny (fastest)`
- `Local: Base (faster)`
- `Local: Small (better quality)`
- `Cloud: GPT-4o Mini Transcribe (faster)`
- `Cloud: GPT-4o Transcribe`
- `Cloud: Whisper-1`

### Translation models

- `Local: Basic Fallback (fastest)`
- `Cloud: GPT-5 Mini`

## Important Notes

- This app is a WPF desktop application, not WinUI 3.
- Local subtitle generation is model-backed through Whisper.NET.
- Local translation is still a basic fallback and is not yet a full offline translation model.
- Translation target language is currently English only.
