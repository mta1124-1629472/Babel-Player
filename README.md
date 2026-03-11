# Babel Player

[Babel Player](https://github.com/mta1124-1629472/Babel-Player/) is a Windows desktop video player for local media with local-first subtitle generation, optional cloud services, and an AI subtitle overlay designed for translation workflows.

The active desktop shell is `src/BabelPlayer.WinUI`.

Canonical repo:
- GitHub: [mta1124-1629472/Babel-Player](https://github.com/mta1124-1629472/Babel-Player/)
- Local solution: `BabelPlayer.sln`
- Public app name: `Babel Player`
- Internal code name: `BabelPlayer`

## Current Scope

What the app does today:
- plays local video files in a WinUI 3 desktop shell backed by an embedded mpv runtime
- auto-loads sidecar subtitles when present
- generates subtitles from audio when no sidecar subtitle file exists
- supports local and cloud transcription model selection
- supports local and cloud translation model selection
- shows subtitles as source-only, translation-only, dual, or hidden
- imports external subtitle files in `SRT`, `VTT`, `ASS`, and `SSA`
- can extract text-based embedded subtitle tracks from media into the Babel Player subtitle pipeline
- exports the current English subtitle result as `SRT`
- persists model selection, auto-translate preference, API credentials, playback settings, and resume state

## Architecture

- `src/BabelPlayer.WinUI`
  - primary WinUI 3 desktop shell
  - embedded mpv playback surface
  - playlist, file browser, transport, command bar, overlays, and shortcut editor
  - file/folder pickers, credential prompts, and window-mode handling
- `src/BabelPlayer.Core`
  - subtitle parsing and export
  - subtitle cue/state models
  - local/cloud transcription
  - local/cloud translation
  - language detection
  - hardware detection

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

Cloud transcription models are available from the WinUI shell transcription picker:
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

Cloud translation credentials for these providers are also validated and stored locally when configured from the WinUI translation picker and provider prompts.

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

The WinUI shell uses a top command bar plus a `Playback` flyout instead of a traditional menu bar.

### Shell commands

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

Use the shortcut editor in the WinUI shell to change and persist them.

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

- Windows 10/11 with media support
- `.NET 8 SDK`
- Visual Studio 2022 or later recommended for development

## Build and Run

From the `babel-player` repository root:

```powershell
dotnet build BabelPlayer.sln
powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

In Visual Studio, set `BabelPlayer.WinUI` as the startup project.

If `BabelPlayer.WinUI` is already running, close it before rebuilding or rerunning.

## Release Artifacts

Tagged GitHub releases publish two Windows distribution formats:

- portable zip
  - extract the archive to any folder and run `BabelPlayer.exe`
- EXE installer
  - run the installer and launch Babel Player from the Start menu or desktop shortcut

The portable build and installer are produced from the same WinUI publish output.

## Known Limitations

- Translation target language is currently English only.
- Embedded subtitle import is implemented for text-based tracks; image-based subtitle tracks are display-only.
- HY-MT first-use model download exposes milestone status, not byte-level progress.
- Real-world ffmpeg stream mapping for some containers may still need tuning on edge cases.
- RTX Video SDK features are not part of the current WinUI migration cleanup.
- The workspace directory on disk may still use an older folder name even though the solution/projects/UI have been renamed to Babel Player / BabelPlayer.

## Additional Docs

- [Install guide](docs/INSTALL.md)
- [Third-party notices](docs/LICENSES.md)
