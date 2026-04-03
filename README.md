# Babel Player

Babel Player is a Windows desktop dubbing workstation for local media.

It takes a source file through:

`source media -> timed transcript -> translated dialogue -> spoken dubbed output -> in-context preview and refinement`

![Babel Player preview](Assets/preview.png)

## Install

Download the latest portable Windows release from [GitHub Releases](https://github.com/mta1124-1629472/Babel-Player/releases/latest).

1. Download `Babel-Player-<version>-win-x64-portable.zip`.
2. Extract it to a folder such as `C:\Apps\BabelPlayer`.
3. Run `BabelPlayer.exe`.

The release bundle includes:

- `BabelPlayer.exe` and managed dependencies
- the .NET runtime needed by the app
- `ffmpeg.exe`
- `libmpv-2.dll`
- bundled inference host assets under `inference/`
- `uv.exe` when present in `tools/win-x64/uv.exe` at publish time

## Compute Modes

Each inference stage now exposes a public `Compute` selector:

- `CPU`: runs the local subprocess path on your machine
- `GPU`: uses the local GPU host path
- `Cloud`: uses a remote provider API or hosted backend

Current phase-1 GPU behavior:

- the default GPU backend is `Managed local GPU`
- the advanced GPU backend is `Docker GPU host`
- GPU transcription and GPU translation are in scope
- GPU TTS remains gated until the host path is fully validated on real hardware

The app does not silently fall back from `GPU` to `CPU`. If GPU is selected and unavailable, the stage stays blocked with a remediation message.

## What Works

- load a local media file
- generate a timed transcript
- translate dialogue
- generate dubbed TTS audio
- preview source media, captions, and dubbed segments in context
- reopen saved sessions and continue working
- export captions as `.srt`

## What Does Not Work Yet

- final video export with muxed dub audio and/or burned-in captions is backend-prepared but not wired through the app yet
- GPU TTS is not publicly exposed in phase 1
- GPU diarization is deferred
- Windows is the only supported desktop platform today

## Requirements

| Scenario | What you need |
|---|---|
| `CPU` local subprocess path | Windows 10/11 x64, ffmpeg, and a working Python install if no managed runtime already exists |
| `GPU` managed local path | Windows 10/11 x64, NVIDIA GPU with CUDA support |
| `GPU` Docker backend | Docker Desktop with Linux engine running, plus NVIDIA container support where applicable |
| `Cloud` providers | the relevant API key(s) |

If you build from source, you also need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## First Run

1. Start the app.
2. Open a local media file.
3. Choose `CPU`, `GPU`, or `Cloud` for each stage.
4. Pick the provider and model or voice for that stage.
5. Add required API keys in Settings if you use cloud providers.
6. Run transcription, then translation, then TTS generation.
7. Toggle dub mode to preview the result.

If you choose `GPU`:

- the default path is the managed local GPU host
- the app can bootstrap that host for you
- Docker is only needed if you explicitly switch the advanced GPU backend to `Docker GPU host`

## Source Build

```powershell
git clone https://github.com/mta1124-1629472/Babel-Player.git
cd Babel-Player
dotnet build
dotnet run --project BabelPlayer.csproj
```

Run the test and architecture checks with:

```powershell
dotnet test
python scripts/check-architecture.py
python -m py_compile inference/main.py
```

## Project Layout

- [App.axaml.cs](App.axaml.cs) owns startup and composition
- [SessionWorkflowCoordinator.cs](Services/SessionWorkflowCoordinator.cs) owns workflow state and stage progression
- [EmbeddedPlaybackViewModel.cs](ViewModels/EmbeddedPlaybackViewModel.cs) owns playback and preview behavior
- [Services](Services) contains providers, persistence, transport, and host-management code
- [docs/smoke](docs/smoke) records manual verification notes

## Contributing

Read these before making changes:

- [AGENTS.md](AGENTS.md)
- [PLAN.md](PLAN.md)
- [docs/containers.md](docs/containers.md)
- [CONTRIBUTING.md](CONTRIBUTING.md)

The project is in active milestone hardening. Changes should preserve the working dubbing loop and keep readiness, hosting, and release behavior truthful.
