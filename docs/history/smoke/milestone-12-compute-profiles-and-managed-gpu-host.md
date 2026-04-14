---
milestone: 12
title: Compute Profiles and Managed Local GPU Host
status: partial
date: 2026-04-03
---

## Metadata
- Scope: public `CPU / GPU / Cloud` profile migration, managed local GPU host bootstrap path, stale-host recovery on Windows, readiness polling/capability warm-up handling, registry/profile filtering, diagnostics wording, settings migration
- Status: `partial`
- Operator: Codex

## Gate Summary
- [x] Public stage selection is profile-based (`CPU / GPU / Cloud`).
- [x] Legacy `Local / Containerized / Cloud` settings migrate to compute profiles.
- [x] Managed local GPU host is wired as the default GPU backend.
- [x] Docker remains available as an advanced GPU backend.
- [x] Registry filtering enforces the NLLB CPU/GPU model split below the UI layer.
- [x] Managed local GPU startup auto-recovers from stale tracked/PID-file host processes before rebuild or restart.
- [x] Managed local GPU startup no longer fails on the first refused connection while the host is still warming.
- [x] Capability probes stay lightweight and can report `live but warming` without marking the host unreachable.
- [x] Build passes.
- [x] Automated tests pass.
- [x] Manual startup smoke passes.
- [ ] Real-hardware managed GPU stale-host recovery and end-to-end GPU inference verification.

## What Was Verified
1. `dotnet build Babel-Player.sln -c Release /p:UseSharedCompilation=false` completed successfully.
2. `dotnet test BabelPlayer.Tests\BabelPlayer.Tests.csproj --no-build --filter FullyQualifiedName~ContainerizedServiceProbeTests|FullyQualifiedName~ContainerizedProvidersTests|FullyQualifiedName~ManagedVenvHostManagerTests` passed with 34 focused probe/provider/managed-host tests.
3. `python scripts/check-architecture.py` passed all checks.
4. `dotnet build BabelPlayer.csproj` completed successfully.
5. `python -m py_compile inference\main.py` completed successfully.
6. Focused regression tests now cover:
   - legacy settings migration into compute profiles and Docker backend selection
   - registry-level NLLB model filtering for CPU vs GPU
   - hidden phase-1 GPU TTS provider behavior
   - managed host compute-type policy (`float16` on CUDA, `int8` on CPU fallback policy)
   - managed host launch args including `--compute-type`
   - stale tracked-host shutdown before restart
   - stale PID-file cleanup before fresh start
   - explicit locked-runtime messaging for Windows `.venv\Scripts` access-denied failures
   - wait-mode probe retries across initial connection-refused responses
   - probe timeout returning the last real unavailable detail instead of a synthetic `Checking`
   - health/live success with capabilities warm-up producing `host is live but capability is still warming`
7. Manual app startup smoke from the earlier managed-host milestone still stands: `dotnet run --project BabelPlayer.csproj -c Release --no-build` launched and remained running until the CLI timeout without startup failure.

## What Was Not Verified
- First-run managed local GPU bootstrap on a clean NVIDIA Windows machine.
- Relaunch behavior when a real stale managed-host process is already bound to port `18000` on Windows.
- Real app-shell validation that backend status transitions from startup/warming to ready on this patched build.
- Real CUDA-backed FasterWhisper and NLLB inference through the managed host.
- Real Docker advanced-backend startup after the profile refactor.
- GPU TTS host endpoints and real-hardware XTTS validation.

## Evidence
- Compute profile settings and migration:
  - `Services/Settings/AppSettings.cs`
  - `Services/Settings/SettingsService.cs`
  - `Services/InferenceRuntimeCatalog.cs`
- Registry/profile enforcement:
  - `Services/Registries/TranscriptionRegistry.cs`
  - `Services/Registries/TranslationRegistry.cs`
  - `Services/Registries/TtsRegistry.cs`
- Managed host bootstrap:
  - `Services/ManagedVenvHostManager.cs`
  - `Services/ManagedHostComputeTypePolicy.cs`
  - `Services/ManagedRuntimeLayout.cs`
  - `inference/main.py`
- App wiring and diagnostics:
  - `App.axaml.cs`
  - `Services/BootstrapDiagnostics.cs`
  - `Models/InferenceMode.cs`
- Test coverage:
  - `BabelPlayer.Tests/RegistryTests.cs`
  - `BabelPlayer.Tests/SettingsServiceTests.cs`
  - `BabelPlayer.Tests/ManagedVenvHostManagerTests.cs`
  - `BabelPlayer.Tests/AppSettingsTests.cs`

## Notes
This pass changes the public product language and the profile-selection contract, but it intentionally does not claim that GPU TTS or GPU diarization are ready. Managed local GPU is now the default backend for `GPU`, while Docker remains an advanced path behind settings.

This update specifically hardens the Windows managed-host lifecycle so stale orphaned host processes do not block `uv venv --clear` during runtime rebuilds. The app now stops stale tracked/PID-file managed-host processes automatically and reports a clear locked-runtime error if the runtime still cannot be unlocked.

This follow-up also changes managed-host readiness semantics so the desktop app keeps polling through short-lived `connection refused` responses and treats a live host with slow `/capabilities` as `live but warming` instead of unreachable. The Python capability endpoint now uses lightweight dependency-presence checks instead of heavyweight cold imports.

## Conclusion
Status: `partial`.

The codebase now reflects the intended user-facing model: `CPU / GPU / Cloud`, with managed local GPU as the default low-friction GPU path. Real GPU bootstrap and end-to-end inference still need hardware-backed smoke verification before this area can be marked complete.

## Deferred Items
- Run a clean-machine NVIDIA smoke for managed GPU bootstrap and transcription/translation.
- Run a real stale-host recovery smoke by leaving the managed host alive, forcing a runtime rebuild, and confirming automatic recovery in the app shell.
- Relaunch the app on this patched build and confirm backend status moves from warming to ready without a false unreachable error.
- Run an advanced-backend Docker smoke after the same profile refactor.
- Complete and validate the XTTS host path before publicly exposing GPU TTS.
