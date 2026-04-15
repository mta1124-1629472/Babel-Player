## Learned User Preferences
- Prefer iterative runtime debugging loops: reproduce issue, then continue from fresh logs.
- Prefer non-hardcoded debug log paths for instrumentation; use env var override with repo-root fallback.
- For continual-learning runs, require strict incremental transcript processing and high-signal-only memory updates.
- Prefer disabling/removing Cursor hooks that interrupt normal workflow (popups or cursor focus changes).
- Prefer canonical language codes in persisted settings and pipeline artifacts (typically lowercase ISO 639-1); keep human-readable labels only in UI catalogs to avoid provider contract and strict string-compare mismatches.
- Prefer custom window chrome that matches Windows 11 expectations: flush edge-to-edge segments, square full hit targets, and even alignment without ragged gaps between adjacent controls.
- For NVIDIA RTX Video, bias defaults toward enabling VSR and RTX HDR when hardware and gates allow; keep RTX HDR and HDR passthrough mutually exclusive in the UI; when RTX HDR is enabled, suppress or disable conflicting secondary HDR processing options (such as tone mapping) that should apply under passthrough instead.
- Prefer Piper and Edge TTS voice download and per-speaker voice assignment through the Speaker Reference Wizard rather than duplicating long voice lists in Settings or the main pipeline controls.

## Learned Workspace Facts
- This workspace uses a project transcript store under the standard Cursor project transcripts location.
- The codebase actively uses both managed CPU and managed GPU inference flows for diarization/transcription debugging.
- The repository no longer uses Git LFS; LFS-related hook assumptions are outdated here.
- Docker support is maintained as a power-user inference-host option; containerizing the desktop app is not the primary runtime model.
- Forward-facing product naming uses `Babel Player` (space, no dash); dev builds append `[DEV]`.
- On Windows, client diagnostics are commonly written to `%LocalAppData%/BabelPlayer/logs/babel-player.log`.
- Per-provider language allowlists and multilingual capability tags are maintained in centralized catalog types in the codebase rather than ad hoc string checks scattered through the pipeline.
- NVIDIA RTX Video features (VSR, RTX HDR) are gated on supported GPU hardware, display HDR state where applicable, and the GPU-accelerated video path (for example `VideoUseGpuNext`-style settings), not on a single flag alone.

## Cursor Cloud specific instructions

### Environment

The Cloud Agent VM runs Ubuntu 24.04 (x64). .NET 10.0 SDK is installed at `$HOME/.dotnet`. In non-interactive shells, `~/.bashrc` may not be sourced automatically, so export `DOTNET_ROOT` and update `PATH` explicitly before running `dotnet` commands. Python 3.12 is available at `/usr/bin/python3`.

### Build / Test / Lint

Standard commands from `CLAUDE.md` and `README.md` work on the Linux VM:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet restore Babel-Player.sln
dotnet build Babel-Player.sln --no-restore
dotnet test Babel-Player.sln --no-build --filter "Category!=Integration&Category!=RequiresPython&Category!=RequiresFfmpeg&Category!=RequiresExternalTranslation"
python3 scripts/check-architecture.py
python3 -m py_compile inference/main.py
```

### Known gotchas

- **`dotnet test` hangs after completion**: The VSTest host process does not exit after all tests finish on Linux. Use `timeout 90 dotnet test ...` or background the process and kill it after ~30 seconds of inactivity. All test results are produced within the first ~15 seconds.
- **Pre-existing test failures**: Several tests fail on `main` independent of environment: Qwen batch endpoint mock tests, timing-sensitive pipeline streaming tests (`PipelineStageProgressTests`), a settings serialization test, and a `ContainerizedServiceProbeTests` cache test. These are not caused by the Linux environment.
- **No GUI on Linux**: This is a Windows desktop app (Avalonia + libmpv). The app cannot be launched graphically on the Cloud Agent VM. Build, test, and lint verification are the appropriate scope for CI-equivalent validation.
- **Native binaries not needed for tests**: `libmpv-2.dll` and `uv.exe` are Windows-only and fetched via `scripts/fetch-win-native-deps.ps1`. They are not needed for building or running the core test suite on Linux.
