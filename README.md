# Babel Player

[Babel Player](https://github.com/mta1124-1629472/Babel-Player/) is a desktop video player for local media with local-first subtitle generation, optional cloud services, and an AI subtitle overlay designed for translation workflows.

The project is migrating from a Windows-only WinUI 3 shell to a cross-platform Avalonia shell backed by libmpv. The **Avalonia shell** (`src/BabelPlayer.Avalonia`) is the active development target on the `avalonia-shell` branch. The legacy WinUI shell (`src/BabelPlayer.WinUI`) remains in the solution for reference.

Canonical repo:
- GitHub: [mta1124-1629472/Babel-Player](https://github.com/mta1124-1629472/Babel-Player/)
- Local solution: `BabelPlayer.sln`
- Public app name: `Babel Player`
- Internal code name: `BabelPlayer`

## Current Scope

What the app does today:
- plays local video files backed by libmpv (direct P/Invoke via `BabelPlayer.Playback.Mpv`)
- auto-loads sidecar subtitles when present
- generates subtitles from audio when no sidecar subtitle file exists
- supports local and cloud transcription model selection
- supports local and cloud translation model selection
- shows subtitles as source-only, translation-only, dual, or hidden
- imports external subtitle files in `SRT`, `VTT`, `ASS`, and `SSA`
- can extract text-based embedded subtitle tracks from media into the Babel Player subtitle pipeline
- exports the current English subtitle result as `SRT`
- persists model selection, auto-translate preference, API credentials, playback settings, and resume state
- file browser and playlist panels with drag-and-drop, queue management, and auto-advance
- configurable keyboard shortcuts with a persisted shortcut profile
- fullscreen, borderless, and picture-in-picture window modes

## Screenshots

![Main window](docs/images/screenshot%201.png)

![Playback menu](docs/images/screenshot%202.png)

## Architecture

### Projects

- `src/BabelPlayer.Avalonia` — cross-platform Avalonia desktop shell (active development target)
  - libmpv playback surface via `NativeControlHost`
  - playlist, file browser, transport bar, toolbar, subtitle overlay, credential prompts
  - keyboard shortcuts, window-mode handling, fullscreen with auto-hiding controls
- `src/BabelPlayer.App` — application domain layer
  - `MediaSession` state model and `MediaSessionCoordinator` mutation boundary
  - orchestrators: `PlaybackBackendCoordinator`, `SubtitleWorkflowController`, `ShellController`, `PlaybackSessionController`, `PlaylistController`
  - shell projections, workflow state stores, provider registries
- `src/BabelPlayer.Infrastructure` — provider adapters and runtime installers
  - transcription/translation provider composition (`ProviderCompositionFactory`)
  - runtime bootstrap: mpv, ffmpeg, llama.cpp installers
  - subtitle source resolution and import
- `src/BabelPlayer.Playback.Mpv` — libmpv P/Invoke backend
  - `LibMpvPlaybackBackend` implements `IPlaybackBackend`
  - `LibMpvNative` P/Invoke declarations and `LibMpvNodeHelpers` marshalling
- `src/BabelPlayer.Core` — shared platform-agnostic services
  - subtitle parsing and export
  - subtitle cue/state models
  - local/cloud transcription and translation engines
  - language detection
  - hardware detection
- `src/BabelPlayer.WinUI` — legacy WinUI 3 desktop shell (reference only)
- `tests/BabelPlayer.App.Tests` — unit and seam tests for orchestrators, projections, and state mutations

### Key Design Principles

- `MediaSession` is the single source of truth for all timed media state
- `MediaSessionCoordinator` is the sole mutation boundary for timed writes
- The shell is view-only — it consumes immutable projections, never writes state directly
- Presenters are stateless adapters that receive pre-decided state from the App layer
- Platform-native code (WinUI, Win32, DirectX) never leaks into App-layer contracts
- Provider adapters (transcription, translation, playback) are swappable via narrow interfaces

See `AGENTS.md`, `docs/ARCHITECTURE.md`, and `docs/DEVELOPMENT_RULES.md` for full details.

## Playback Workflow

### Open media

Use the command bar `Open` or `Folder` actions, drag files/folders into the window, or add items to the playlist/browser.

When a video opens, Babel Player:
- loads the file into the embedded mpv player
- checks for a matching sidecar `.srt`
- loads the sidecar subtitle track if it exists
- otherwise starts automatic caption generation from audio
- restores playback position when a saved resume point exists and is valid

### Playlist and browser

- The file browser and playlist panels are collapsed by default.
- Use `View` to show or hide either panel.
- Visibility persists across sessions.
- Folder adds are recursive for supported video extensions.
- Playlist auto-advances when playback ends.

### Resume playback

Resume entries are stored under local app data and are restored for partially watched files.

Behavior:
- saved during playback and on app close
- resumed only for meaningful unfinished positions
- cleared when a video is effectively finished

## Subtitle Sources

Babel Player can work from several subtitle sources:

- sidecar `.srt`
- manually loaded external subtitle files: `SRT`, `VTT`, `ASS`, `SSA`
- auto-generated captions from video audio
- extracted embedded text subtitle tracks from MKV/MP4-compatible containers

Notes:
- text-based embedded subtitle tracks can be imported into the Babel Player AI subtitle pipeline
- image-based embedded subtitle tracks can still be selected for direct playback, but they are not translated by the overlay pipeline

## Subtitle Overlay Modes

`Subtitles > Render Mode` supports:
- `Off`
- `Source Only`
- `Translation Only`
- `Dual`

`Dual` shows source text above translated text when both are meaningfully different.

## Subtitle Styling

`Subtitles > Style` currently supports:
- larger/smaller text
- more/less subtitle background
- raise/lower subtitle position
- translation color presets

These settings persist between sessions.

## Transcription Models

### Local transcription

Local transcription uses Whisper.NET with English-only Whisper models:
- `Local: Tiny (fastest)`
- `Local: Base (faster)`
- `Local: Small (better quality)`

These models download on first use and are then reused locally.

### Cloud transcription

Cloud transcription models are available from the transcription picker:
- `Cloud: GPT-4o Mini Transcribe (faster)`
- `Cloud: GPT-4o Transcribe`
- `Cloud: Whisper-1`

Cloud transcription requires a valid OpenAI API key.

## Translation Models

### Local translation

Local translation currently uses HY-MT models through `llama-server`:
- `Local: HY-MT1.5 1.8B`
- `Local: HY-MT1.5 7B`

Behavior:
- selecting a HY-MT model can bootstrap `llama-server` automatically
- Babel Player can install the pinned llama.cpp runtime itself
- HY-MT model acquisition is delegated to `llama-server`
- local translation has no fake fallback option anymore; if no real translation backend is configured, nothing is selected

### Cloud translation

Cloud translation options currently include:
- `Cloud: OpenAI GPT-5 Mini`
- `Cloud: Google Translate`
- `Cloud: DeepL API`
- `Cloud: Microsoft Translator`

These are independent from subtitle transcription selection.

## Credentials and Runtime Bootstrap

### OpenAI

Used for:
- cloud transcription
- OpenAI cloud translation

Behavior:
- prompted only when needed
- validated before being saved
- persisted between sessions

### Google, DeepL, Microsoft Translator

Cloud translation credentials for these providers are also validated and stored locally when configured from the translation picker and provider prompts.

### llama.cpp / HY-MT

For local HY-MT translation:
- Babel Player can guide runtime setup
- `llama-server` can be installed automatically or selected manually
- runtime state is surfaced in the UI
- first-use HY-MT model download/loading status is surfaced as milestone text

### mpv and ffmpeg

Babel Player bootstraps these runtimes when needed:
- `mpv` for playback
- `ffmpeg` for subtitle conversion and embedded text subtitle extraction

The app surfaces runtime download and extraction progress when it owns the transfer.

## Command Surface

The Avalonia shell uses a top toolbar plus transport bar.

### Toolbar commands

- `Open`
- `Folder`
- `Import Subs`
- `Subtitles`
- `Immersive`
- `Fullscreen`
- `PiP`

### Playback flyout

- audio track selection
- embedded subtitle track selection
- hardware decoding mode
- aspect ratio controls
- audio/subtitle delay adjustments
- subtitle render mode and style controls
- transcription and translation model controls
- resume playback toggle
- subtitle export
- picture-in-picture
- fullscreen

### Secondary commands

- show/hide browser panel
- show/hide playlist panel
- add videos root

## Keyboard Shortcuts

Babel Player now uses a persisted shortcut profile rather than only hardcoded bindings.

Default bindings include:
- play/pause
- small and large seek
- previous/next frame
- speed up/down/reset
- subtitle toggle
- translation toggle
- subtitle delay adjust
- audio delay adjust
- fullscreen
- picture-in-picture
- next/previous playlist item
- mute

Use the shortcut editor to change and persist them.

## Playback Controls

The bottom transport includes:
- open file
- previous/next playlist item
- fullscreen
- picture-in-picture
- rewind / fast-forward
- frame stepping
- play / pause
- playback speed selector
- mute and volume
- seek bar with current time and duration

The transport auto-hides during active playback and reappears on hover, pause, or seeking.

## Hardware and Rendering

The app reports the active accelerator summary in the top status area.

Current rendering/playback controls include:
- hardware decoding mode selection
- aspect ratio overrides
- borderless and picture-in-picture window modes
- fullscreen

## Build Requirements

- Windows 10/11 (Linux/macOS support is the migration goal but not yet validated)
- `.NET 9 SDK`
- Visual Studio 2022 or later, or any editor with .NET CLI
- libmpv runtime DLL (`libmpv-2.dll`) expected in `native/win-x64/`

## Build and Run

### Avalonia shell (active)

From the repository root:

```powershell
dotnet build BabelPlayer.sln
dotnet run --project src/BabelPlayer.Avalonia
```

### Legacy WinUI shell

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

Or in Visual Studio, set `BabelPlayer.WinUI` as the startup project.

### Tests

```powershell
dotnet test BabelPlayer.sln
```

## Release Artifacts

Tagged GitHub releases publish two Windows distribution formats:

- portable zip
  - extract the archive to any folder and run `BabelPlayer.exe`
- EXE installer
  - run the installer and launch Babel Player from the Start menu or desktop shortcut

The portable build and installer are produced from the same WinUI publish output.

## Known Limitations

- Translation target language is currently English only.
- The Avalonia shell is the active development target; some WinUI-only features (shortcut editor dialog, some polish items) are still being ported.
- Cross-platform builds (Linux/macOS) are an architectural goal but not yet validated at runtime.
- libmpv runtime DLL must be present locally; it is not distributed via NuGet.
- Embedded subtitle import is implemented for text-based tracks; image-based subtitle tracks are display-only.
- HY-MT first-use model download exposes milestone status, not byte-level progress.
- Real-world ffmpeg stream mapping for some containers may still need tuning on edge cases.
- RTX Video SDK features are not part of the current WinUI migration cleanup.
- The workspace directory on disk may still use an older folder name even though the solution/projects/UI have been renamed to Babel Player / BabelPlayer.

## Additional Docs

- [Install guide](docs/INSTALL.md)
- [Third-party notices](docs/LICENSES.md)
