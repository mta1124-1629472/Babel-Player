# Babel Player

[![Sponsor](https://img.shields.io/github/sponsors/mta1124-1629472?label=Sponsor&logo=GitHub)](https://github.com/sponsors/mta1124-1629472)
[![CI](https://github.com/mta1124-1629472/Babel-Player/actions/workflows/ci.yml/badge.svg)](https://github.com/mta1124-1629472/Babel-Player/actions/workflows/ci.yml)
[![GitHub Release](https://img.shields.io/github/v/release/mta1124-1629472/Babel-Player)](https://github.com/mta1124-1629472/Babel-Player/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Alpha](https://img.shields.io/badge/status-early%20alpha-orange)](#status)

Babel Player is a Windows desktop dubbing workstation for local video files. It takes source media through transcription, translation, TTS generation, and in-context preview in a single session.

Babel Deck is built and maintained by a solo developer. If you’d like to support its continued development:
[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/R5R01WOOYW)

![Babel Player preview](Assets/preview.png)

The workflow is:

`source media -> timed transcript -> translated dialogue -> spoken dubbed output -> preview and refinement`

## Install from GitHub Releases

If you want to run Babel Player without building from source, use the latest GitHub release.

1. Download the `Babel-Player-<version>-win-x64-portable.zip` asset from the release page.
2. Download the matching `.sha256` file and verify the archive if you want integrity checking.
3. Extract the zip to a folder such as `C:\Apps\BabelPlayer`.
4. Run `BabelPlayer.exe`.

Release bundles include:

- the app executable and managed dependencies
- the .NET runtime needed to run the app
- `ffmpeg.exe`
- `libmpv-2.dll`

Release bundles do not include Python or any external/local provider runtimes. Those still need to be installed and reachable on your machine.

## What It Does

- Load a local video file
- Generate a timed transcript with Whisper via Python
- Translate and adapt dialogue for spoken delivery
- Generate dubbed TTS audio per segment
- Preview the result in context with a subtitle overlay and dub mode
- Persist and restore sessions between launches
- Export captions as `.srt`

## What It Does Not Do Yet

- No automatic end-to-end video export yet
- No audio mixing between source audio and dubbed audio
- No multi-language UI
- Windows only

## Requirements

| Dependency | Notes |
|-----------|-------|
| Windows 10/11 x64 | Only supported platform today |
| Python 3.10+ | Required for transcription, translation, and TTS |
| Whisper-compatible Python environment | Transcription backend |
| FFmpeg | Audio extraction and media tooling |
| `libmpv-2.dll` | Bundled in `native/win-x64/` for playback |

If you are building from source, you also need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Release bundles already include the runtime.

## First Run

1. Start the app.
2. Open a local video file.
3. Configure providers and any required API keys in Settings.
4. Run transcription, then translation, then TTS generation.
5. Toggle dub mode to preview the result in the player.

If the app reports missing dependencies at startup, install the required toolchain and verify it is on `PATH` or available in the app folder.

## Source Build

```bash
git clone https://github.com/mta1124-1629472/Babel-Player.git
cd Babel-Player
dotnet build
dotnet run --project BabelPlayer.csproj
```

Run the tests with:

```bash
dotnet test
```

## Project Layout

- `App.axaml.cs` owns startup and composition
- `Services/SessionWorkflowCoordinator.cs` owns workflow state and stage progression
- `ViewModels/EmbeddedPlaybackViewModel.cs` owns the playback and preview surface logic
- `Services/` contains provider adapters, persistence, and media services
- `docs/` contains smoke notes and deployment notes

## Release Notes For Users

The release bundle is portable. You can extract it anywhere and launch the executable directly. Session data and settings are stored locally under your user profile, so reopening the app will restore prior work when the underlying artifacts still exist.

## Contributing

Read these before making changes:

- [`AGENTS.md`](AGENTS.md)
- [`PLAN.md`](PLAN.md)
- [`docs/architecture.md`](docs/architecture.md)
- [`CONTRIBUTING.md`](CONTRIBUTING.md)

The project is still in early alpha, but it is no longer a prototype. Changes should preserve the working source-media-to-dub workflow and keep release behavior truthful.
