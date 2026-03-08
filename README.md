# BabelPlayer

BabelPlayer is a Windows desktop video player for local files that can:

- play video locally
- load existing `.srt` subtitles
- auto-generate subtitles when no `.srt` exists
- translate subtitles into English on demand
- export the English subtitle track as `.srt`

The current app is a WPF application targeting `.NET 8` on Windows.

## Current Status

What works today:

- Local video playback for common formats such as `.mp4`, `.mkv`, `.mov`, `.avi`, and `.webm`
- Sidecar subtitle auto-load when a matching `.srt` is present
- Manual subtitle loading from the `Subtitles` menu
- Automatic subtitle generation from video audio when no `.srt` exists
- Separate transcription and translation pipelines
- Local and cloud transcription model selection
- Local and cloud translation model selection
- Real-time subtitle overlay during playback
- English subtitle export
- Persisted OpenAI API key storage for cloud features
- Bottom transport controls with seek, rewind, fast-forward, fullscreen, and volume

## Architecture

- `src/BabelPlayer.UI`
  - WPF desktop UI
  - video playback
  - subtitle overlay
  - transcription and translation menu state
  - transport controls
- `src/BabelPlayer.Core`
  - subtitle parsing and export
  - local/cloud transcription
  - local/cloud translation
  - language detection
  - hardware detection

## How The App Works

### 1. Open a video

Use the `Open` button in the bottom transport bar.

When a video opens, the app:

- plays the local file
- looks for a sidecar `.srt` next to the video
- loads that subtitle track if present
- otherwise starts automatic subtitle generation from the video audio

### 2. Subtitle source behavior

If an `.srt` exists:

- the `.srt` is treated as the source subtitle track
- the app detects the source language
- translation only happens if you enable it

If no `.srt` exists:

- the app generates source subtitles from audio
- generated subtitles are then either shown directly or translated, depending on your translation settings

### 3. Translation behavior

Translation is separate from subtitle generation.

By default:

- `Translate Current Video` is off
- `Auto-Translate Videos Not In > English` is off

This means the app does not translate anything unless you explicitly enable translation.

If `Auto-Translate Videos Not In > English` is enabled:

- the app waits for source language detection
- if the detected source language is not English, translation is enabled for that video

## Menus

### Subtitles

- `Show Subtitles`
  - toggles the subtitle overlay
- `Open Subtitles`
  - manually load an `.srt`
- `Transcription Model`
  - `Local: Tiny (fastest)`
  - `Local: Base (faster)`
  - `Local: Small (better quality)`
  - `Cloud: GPT-4o Mini Transcribe (faster)`
  - `Cloud: GPT-4o Transcribe`
  - `Cloud: Whisper-1`
- `Set OpenAI API Key`

### Translation

- `Translate Current Video`
- `Target Language > English`
- `Auto-Translate Videos Not In > English`
- `Translation Model`
  - `Local: Basic Fallback (fastest)`
  - `Cloud: GPT-5 Mini`
- `Set OpenAI API Key`
- `Export EN SRT`

## Local And Cloud Models

### Transcription / subtitle generation

Local subtitle generation uses Whisper.NET with English-only Whisper variants:

- `Tiny.en`
- `Base.en`
- `Small.en`

Cloud subtitle generation can use:

- `gpt-4o-mini-transcribe`
- `gpt-4o-transcribe`
- `whisper-1`

### Translation

Cloud translation uses OpenAI Responses with:

- `gpt-5-mini`

Local translation is currently:

- a basic offline fallback
- not a full local neural machine translation model yet

That distinction matters:

- local subtitle generation is real model-backed transcription
- local translation is still placeholder quality compared with cloud translation

## OpenAI API Key

An OpenAI API key is only required when you choose a cloud model from either menu.

Behavior:

- if you select a cloud transcription or translation model and no key is available, the app prompts for one
- valid keys are saved locally for reuse across sessions
- invalid keys are rejected
- cloud failures can force the app back to a local default for the current session

The app first checks `OPENAI_API_KEY`, then falls back to its saved local key store.

## Playback UI

The bottom-center transport bar includes:

- `Open`
- rewind `10s`
- play / pause
- fast-forward `10s`
- seek bar
- current time and duration
- fullscreen
- volume

The transport auto-hides during active playback and reappears when:

- the mouse is over the playback surface
- playback is paused
- the user is seeking

## Subtitle Overlay

- subtitles render at the bottom of the video surface
- the overlay can be hidden completely with `Show Subtitles`
- when hidden, the subtitle bar collapses fully instead of leaving a black strip
- during caption generation, the app keeps status text visible so progress does not look like a failure

## Hardware

The app detects the active hardware accelerator and reports a simplified runtime label such as:

- `CPU`
- a GPU name
- an NVIDIA GPU with `CUDA`

This is informational today. It helps explain which accelerator the runtime is likely using.

## Build Requirements

- Windows 11 or a current Windows 10/11 environment with media support
- `.NET 8 SDK`
- Visual Studio 2022 or later is recommended for development

NuGet packages currently include:

- `Whisper.net`
- `Whisper.net.Runtime`
- `Whisper.net.Runtime.Cuda`
- `NAudio`
- `System.Speech`
- `System.Management`
- `Microsoft.ML.OnnxRuntime`
- `Microsoft.ML.OnnxRuntime.DirectML`

## Build And Run

From the repository root:

```powershell
dotnet build BabelPlayer.sln
dotnet run --project src/BabelPlayer.UI
```

If the app is already running, stop it before rebuilding or the executable may be locked.

## Recommended Usage

### Best local-only subtitle generation

- use `Subtitles > Transcription Model > Local: Base (faster)` as the default balance
- use `Local: Tiny (fastest)` if speed matters more than accuracy
- use `Local: Small (better quality)` if your machine can tolerate slower inference

### Best current translation quality

- enable translation only when needed
- choose `Translation > Translation Model > Cloud: GPT-5 Mini`

### If your video already has subtitles

- prefer the existing `.srt`
- treat it as the source subtitle track
- translate only if the source language is not already English

## Known Limitations

- Local translation is still a basic fallback, not a full offline MT model.
- Local subtitle quality depends heavily on audio quality and the selected Whisper model.
- Cloud features require valid OpenAI credentials and usable account quota.
- Translation target language is currently English only.
- The README documents current behavior, not every historical experiment during development.

## Recommended Next Upgrade

The next major quality improvement for local translation should be replacing the current fallback translator with a real offline translation model.

Recommended candidate:

- `NLLB-200 distilled 600M`

Reason:

- it is an actual multilingual translation model
- it is a much better fit for subtitle translation than the current local fallback
- it is a better quality choice than keeping heuristic local translation logic

If easier deployment matters more than translation quality, `Argos Translate` is the simpler alternative.

## Additional Docs

- [Install guide](docs/INSTALL.md)
- [Third-party notices](docs/LICENSES.md)
