---
milestone: 12
title: Compute Profiles and Managed Local GPU Host
status: partial
date: 2026-04-02
---

## Metadata
- Scope: public `CPU / GPU / Cloud` profile migration, managed local GPU host bootstrap path, registry/profile filtering, diagnostics wording, settings migration
- Status: `partial`
- Operator: Codex

## Gate Summary
- [x] Public stage selection is profile-based (`CPU / GPU / Cloud`).
- [x] Legacy `Local / Containerized / Cloud` settings migrate to compute profiles.
- [x] Managed local GPU host is wired as the default GPU backend.
- [x] Docker remains available as an advanced GPU backend.
- [x] Registry filtering enforces the NLLB CPU/GPU model split below the UI layer.
- [x] Build passes.
- [x] Automated tests pass.
- [x] Manual startup smoke passes.
- [ ] Real-hardware managed GPU bootstrap and end-to-end GPU inference verification.

## What Was Verified
1. `dotnet build Babel-Player.sln -c Release /p:UseSharedCompilation=false` completed successfully.
2. `dotnet test BabelPlayer.Tests\BabelPlayer.Tests.csproj -c Release /p:UseSharedCompilation=false` passed with 354 tests.
3. `python scripts/check-architecture.py` passed all checks.
4. `python -m py_compile inference\main.py` completed successfully.
5. Focused regression tests now cover:
   - legacy settings migration into compute profiles and Docker backend selection
   - registry-level NLLB model filtering for CPU vs GPU
   - hidden phase-1 GPU TTS provider behavior
   - managed host compute-type policy (`float16` on CUDA, `int8` on CPU fallback policy)
   - managed host launch args including `--compute-type`
6. Manual app startup smoke succeeded: `dotnet run --project BabelPlayer.csproj -c Release --no-build` launched and remained running until the CLI timeout without startup failure.

## What Was Not Verified
- First-run managed local GPU bootstrap on a clean NVIDIA Windows machine.
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

## Conclusion
Status: `partial`.

The codebase now reflects the intended user-facing model: `CPU / GPU / Cloud`, with managed local GPU as the default low-friction GPU path. Real GPU bootstrap and end-to-end inference still need hardware-backed smoke verification before this area can be marked complete.

## Deferred Items
- Run a clean-machine NVIDIA smoke for managed GPU bootstrap and transcription/translation.
- Run an advanced-backend Docker smoke after the same profile refactor.
- Complete and validate the XTTS host path before publicly exposing GPU TTS.
