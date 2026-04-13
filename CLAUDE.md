# Babel Player — Claude Context

## Commands

```bash
dotnet clean Babel-Player.sln
dotnet build Babel-Player.sln             # Full build (includes restore)
dotnet build Babel-Player.sln --no-restore # Fast build (skip restore)
dotnet test Babel-Player.sln             # Run all tests
dotnet run --project BabelPlayer.csproj   # Launch the app
python3 scripts/check-architecture.py    # Architecture linter
```

## Troubleshooting Build Issues

If the build fails with a "process cannot access the file" error (typically locked by `clrdbg.exe` or `.NET Host`), run the following command to force-clear the locks:

```powershell
taskkill /F /IM clrdbg.exe /IM dotnet.exe
```

## Essential Reading

Before making non-trivial changes, read:
- `AGENTS.md` — operating rules, scope discipline, non-negotiables
- `PLAN.md` — milestone order and gates (the plan wins if anything conflicts)
- `docs/architecture.md` — structural boundaries and state ownership

## Current Milestone

**Milestones 1–9** are complete. **Milestones 10 and 11** are partially complete (settings/bootstrap and local-offline expansion — see `docs/smoke/` for gate evidence on each).

**Milestone 12 — Runtime Optimization and Hardware Routing** is in progress. Compute profiles (`CPU/GPU/Cloud`) and managed local GPU host are implemented; real NVIDIA hardware validation and live container smoke tests are still pending.

## Project Overview

Babel Player is a cross-platform desktop dubbing/localization app built with:
- **C# / .NET 10.0** + **Avalonia 12.0 RC1** (Fluent theme)
- **libmpv** (native C library, P/Invoke) for media playback
- **CommunityToolkit.MVVM 8.2.1** for observable properties
- **Python subprocesses** for AI inference (Faster-Whisper, googletrans, edge-tts)
- **xUnit 2.9.3** + coverlet for testing

Core workflow chain: `source media → ingest → transcribe → translate → TTS → preview → persist`

## Directory Structure

```
Babel-Player/
├── Models/                          # Domain data structures
├── Services/                        # Coordination, infrastructure, AI/media boundaries
├── ViewModels/                      # MVVM coordinators
├── Views/                           # Avalonia XAML UI
├── BabelPlayer.Tests/               # xUnit integration tests
├── docs/
│   ├── architecture.md              # Structural principles
│   ├── smoke/                       # Milestone completion evidence (required)
│   └── containers.md                # WSL/container deployment notes (deferred)
├── scripts/
│   └── check-architecture.py        # Architecture linter
├── native/win-x64/libmpv-2.dll      # Bundled native binary
├── test-assets/video/sample.mp4     # Test media (43KB Spanish TTS video)
├── AGENTS.md                        # Operating discipline (300+ lines — read it)
├── PLAN.md                          # 13-milestone roadmap
└── BabelPlayer.csproj               # net10.0, WinExe, RootNamespace=Babel.Player
```

## Naming Conventions

| Context | Form |
|---------|------|
| Product / branding | `Babel Player` |
| Repository | `Babel-Player` |
| .NET namespaces / assembly IDs | `BabelPlayer` or `Babel.Player` |
| Filenames / folders | match local convention already in use |

## Key Files

| File | Purpose |
|------|---------|
| `Services/SessionWorkflowCoordinator.cs` | **Single owner of all workflow/session state** — all stage progression runs through here |
| `ViewModels/EmbeddedPlaybackViewModel.cs` | Largest VM (~600 lines); manages video playback UI, segment selection, dub mode, subtitle toggle |
| `Models/WorkflowSessionSnapshot.cs` | Complete session state record persisted to disk |
| `Models/SessionWorkflowStage.cs` | Enum: `Foundation → MediaLoaded → Transcribed → Translated → TtsGenerated` |
| `Models/PlaybackState.cs` | Enum: `Idle`, `PlayingSingleSegment`, `PlayingSequence` |
| `Models/WorkflowSegmentState.cs` | Record: segment ID, timing, source/translated text, TTS status |
| `Services/IMediaTransport.cs` | Abstraction for load/play/pause/seek + subtitle + events |
| `PLAN.md` | Milestone gates — current milestone is the only allowed scope |
| `docs/smoke/` | Required gate evidence for each milestone |
| `AGENTS.md` | Non-negotiable operating rules |

## Services Reference

| Service | Responsibility |
|---------|----------------|
| `SessionWorkflowCoordinator` | State owner; orchestrates transcription, translation, TTS; manages multi-file caching; segment playback via two IMediaTransport instances |
| `SessionSnapshotStore` | JSON persistence to `%LOCALAPPDATA%/BabelPlayer/state/`; corruption recovery (moves bad JSON to `.corrupt`) |
| `AppLog` | Thread-safe file logging (`%LOCALAPPDATA%/BabelPlayer/logs/babel-player.log`) |
| `LibMpvHeadlessTransport` | Headless libmpv (`vo=null`, `ao=null`); used for TTS segment audio playback |
| `LibMpvEmbeddedTransport` | GPU-accelerated libmpv (`vo=gpu`, `wid`); renders video to native HWND |
| `TranscriptionService` | Subprocess → Faster-Whisper; auto-detects source language; returns timed segments |
| `TranslationService` | Subprocess → googletrans; supports full-transcript and single-segment regeneration |
| `TtsService` | Subprocess → edge-tts; generates MP3 per segment; supports single-segment regeneration |
| `SrtGenerator` | Static utility; converts segment list to SRT; prefers translated text, falls back to source |

## Python/C# Serialization Contract

**Critical:** Field names crossing the Python/C# boundary are explicit serialization contracts. Do NOT rely on implicit .NET casing.

- Python emits snake_case/camelCase field names
- C# deserializes with `PropertyNameCaseInsensitive = true` OR explicit `[JsonPropertyName]`
- Segment IDs are derived from transcript start time: `segment_{start}` (e.g., `segment_0.0`, `segment_3.68`) — must match Python output exactly as they key the TTS segment dictionary
- When changing field names in Python scripts or C# result records, update **both sides deliberately**

## Artifact Storage

- Session state: `%LOCALAPPDATA%/BabelPlayer/state/current-session.json`
- Session artifacts: `%LOCALAPPDATA%/BabelPlayer/sessions/{SessionId}/`
  - Transcripts, translations, TTS audio in session-specific subdirectories
- On restore, coordinator validates artifacts exist and **downgrades stage** if missing

## Testing

```bash
dotnet test Babel-Player.sln             # All tests
dotnet test Babel-Player.sln --filter "ClassName=SessionWorkflowTests"
```

- 22 integration tests in `BabelPlayer.Tests/SessionWorkflowTests.cs`
- Shared fixture via `SessionWorkflowTemplateFixture` (temp dirs, reusable templates)
- xUnit collection `"Media transport"` runs non-parallel (hardware resource)
- Test assets: `test-assets/video/sample.mp4`

## Architecture Linter

```bash
python3 scripts/check-architecture.py
```

Enforces: `.csproj` structure, test project references, `OutputType=WinExe`, `NotImplementedException` carries PLACEHOLDER messages, silent event stubs have PLACEHOLDER comments.

## Active Skills

| Skill | Purpose |
|-------|---------|
| `proactivity-proactive-agent` | State lives in `~/proactivity/`. Read `memory.md` + `session-state.md` before non-trivial tasks. Leave a next move in state after meaningful work. |
| `self-improving-proactive-agent` | State lives in `~/self-improving/`. Log corrections and self-reflections. Promote patterns after 3x use. |

## Gotchas

- **State ownership:** Never scatter session/workflow state across views or helpers. `SessionWorkflowCoordinator` is the explicit and sole owner.
- **Smoke notes:** Live in `docs/smoke/milestone-NN-label.md`. Status must be `complete`, `partial`, or `failed` — nothing vague. Required sections: Metadata, Gate Summary, What Was Verified, What Was Not Verified, Evidence, Notes, Conclusion, Deferred Items.
- **Scope discipline:** AGENTS.md rules are non-negotiable. Refactors, abstractions, and scope expansion require explicit justification against the current milestone.
- **Fake readiness is forbidden:** Use explicit placeholders or disabled states — never silent fallback or pretend-complete UI.
- **Python/C# JSON contracts:** Field names are explicit serialization contracts (see section above).
- **Segment ID contract:** `segment_{start}` format — changing this breaks TTS segment lookup.
- **No premature architecture:** No provider matrices, factory systems, plugin architectures, or runtime selection systems until milestones earn them.
- **Do not push to main without explicit instruction.** Always develop on the designated branch.
