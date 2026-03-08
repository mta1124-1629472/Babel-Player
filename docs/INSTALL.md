# Install and Run

Canonical repository:
- GitHub: [mta1124-1629472/Babel-Player](https://github.com/mta1124-1629472/Babel-Player/)
- Solution: `BabelPlayer.sln`
- App name: `Babel Player`

## Requirements

1. Windows with media playback support.
2. `.NET 8 SDK`.
3. Visual Studio 2022 or later is recommended if you want to run and debug from the IDE.

## Build

From the `babel-player` repository root:

```powershell
dotnet build BabelPlayer.sln
```

## Run

```powershell
dotnet run --project src/BabelPlayer.UI
```

If `BabelPlayer.UI` is already running, close it before rebuilding or rerunning.

## First-run runtime bootstrap

Depending on the features you use, Babel Player may bootstrap native runtimes on first use:

- `mpv` for playback
- `ffmpeg` for subtitle conversion and embedded text subtitle extraction
- `llama.cpp` / `llama-server` for local HY-MT translation
- local Whisper subtitle models for local transcription

When Babel Player owns the download stream, it shows explicit progress in the UI.

## First-run workflow

1. Open a local video with the bottom `Open` button, or drag files/folders into the window.
2. If a matching sidecar subtitle file exists, Babel Player loads it automatically.
3. If no sidecar subtitle file exists, Babel Player generates subtitles from the audio.
4. Use `Subtitles > Transcription Model` to choose local or cloud subtitle generation.
5. Use `Translation` to enable translation for the current video or automatically for non-English videos.
6. If you choose a cloud model, enter provider credentials when prompted.
7. Use `Playback > Embedded Subtitle Track` to import a text-based embedded track into the Babel Player subtitle pipeline.
8. Use `Translation > Export EN SRT` to save the current English subtitle result.

## Current model options

### Subtitle / transcription models

- `Local: Tiny (fastest)`
- `Local: Base (faster)`
- `Local: Small (better quality)`
- `Cloud: GPT-4o Mini Transcribe (faster)`
- `Cloud: GPT-4o Transcribe`
- `Cloud: Whisper-1`

### Translation models

Local:
- `Local: HY-MT1.5 1.8B`
- `Local: HY-MT1.5 7B`

Cloud:
- `Cloud: OpenAI GPT-5 Mini`
- `Cloud: Google Translate`
- `Cloud: DeepL API`
- `Cloud: Microsoft Translator`

## Important notes

- This app is a WPF desktop application.
- The playback shell is mpv-based, not `MediaElement`-based anymore.
- External subtitle import supports `SRT`, `VTT`, `ASS`, and `SSA`.
- Embedded subtitle import into the AI pipeline is currently for text-based tracks only.
- Translation target language is currently English only.
- Browser and playlist side panels are collapsed by default and can be enabled from `View`.
