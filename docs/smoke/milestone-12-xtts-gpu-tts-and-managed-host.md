---
milestone: 12
title: XTTS GPU TTS and Managed Host Startup
status: partial
date: 2026-04-02
---

## Metadata
- Scope: XTTS GPU TTS provider/model exposure, managed GPU host XTTS endpoints, speaker-reference compatibility, managed-host startup diagnostics
- Status: `partial`
- Operator: Codex

## Gate Summary
- [x] GPU TTS provider is surfaced in the registry/UI contract.
- [x] XTTS model (`xtts-v2`) is exposed as the GPU TTS model.
- [x] XTTS request contract carries model and target language.
- [x] Multi-speaker speaker-reference routing is honored by the XTTS provider path.
- [x] Managed local GPU host exposes XTTS endpoints and TTS capability reporting.
- [x] Managed host startup surfaces explicit CUDA/runtime validation failures instead of only probe refusals.
- [x] Build passes.
- [x] Automated tests pass.
- [ ] Real managed-host XTTS synthesis verified on CUDA hardware.

## What Was Verified
1. `dotnet build Babel-Player.sln -c Release /p:UseSharedCompilation=false /nodeReuse:false` passed.
2. `dotnet test BabelPlayer.Tests\BabelPlayer.Tests.csproj -c Release /p:UseSharedCompilation=false` passed with 357 tests.
3. `python scripts/check-architecture.py` passed.
4. `python -m py_compile inference\main.py` passed.
5. Focused tests now cover:
   - GPU TTS registry exposure for `xtts-container`
   - XTTS segment requests carrying `model` and `language`
   - XTTS combined synthesis honoring per-speaker reference clips
   - managed host launch args including `--compute-type` and `--require-cuda`
   - managed runtime validation failures surfacing explicit CUDA errors
6. Direct probe of the existing local managed runtime at `%LOCALAPPDATA%\BabelPlayer\runtime\managed-gpu\.venv` returned:
   - `cuda_available=false`
   - `cuda_version=null`
   - `tts_installed=false`

## What Was Not Verified
- Successful managed-host bootstrap on this machine after the new GPU requirements manifest is applied.
- Real XTTS synthesis through `http://127.0.0.1:18000` on CUDA hardware.
- End-to-end app click-through with speaker reference clips in the shell.

## Evidence
- TTS registry/runtime exposure:
  - `Services/Registries/TtsRegistry.cs`
  - `Services/InferenceRuntimeCatalog.cs`
- XTTS provider/model/language wiring:
  - `Services/StageContracts.cs`
  - `Services/SessionWorkflowCoordinator.cs`
  - `Services/XttsContainerTtsProvider.cs`
  - `Services/ContainerizedInferenceClient.cs`
- Managed host startup validation:
  - `Services/ManagedVenvHostManager.cs`
  - `Services/DependencyLocator.cs`
- XTTS host endpoints:
  - `inference/main.py`
  - `inference/gpu-requirements.txt`
- Regression coverage:
  - `BabelPlayer.Tests/RegistryTests.cs`
  - `BabelPlayer.Tests/ContainerizedProvidersTests.cs`
  - `BabelPlayer.Tests/ManagedVenvHostManagerTests.cs`

## Notes
The code now treats XTTS GPU TTS as a real public GPU option instead of a hidden internal provider. The current local runtime probe shows that this machine still needs a fresh managed-runtime bootstrap and a CUDA-visible Torch environment before the managed host can actually come up.

## Conclusion
Status: `partial`.

The XTTS GPU TTS wiring and managed-host diagnostics are implemented and regression-tested, but real hardware validation is still outstanding on this machine because the existing managed runtime currently reports no CUDA and no installed XTTS package.

## Deferred Items
- Launch the app once to trigger managed-runtime rebootstrap from the updated `gpu-requirements.txt`.
- Verify `torch.cuda.is_available()` is true inside the rebuilt managed runtime.
- Run an end-to-end GPU TTS pass with real speaker reference clips.
