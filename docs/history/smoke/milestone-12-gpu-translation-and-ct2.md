---
milestone: 12
title: GPU Translation Readiness and CTranslate2 Local Translation
status: partial
date: 2026-04-03
---

## Metadata
- Scope: managed GPU translation readiness alignment, shared-host compute-type policy, lightweight local CTranslate2 translation provider, model preparation flow, registry/UI wiring
- Status: `partial`
- Operator: Codex

## Gate Summary
- [x] Managed GPU translation readiness matches the runtime compute-type constraint.
- [x] GPU translation routing logs explicit provider/model/capability detail before execution.
- [x] A lightweight local CTranslate2 translation provider is exposed in the registry/UI contract.
- [x] CTranslate2 model preparation is wired through the existing model-download surface.
- [x] Build passes.
- [x] Automated tests pass.
- [ ] Manual CUDA-backed GPU translation smoke.
- [ ] Manual local CTranslate2 click-through smoke in the app shell.

## What Was Verified
1. `python scripts/check-architecture.py` passed all checks.
2. `dotnet build BabelPlayer.csproj` passed.
3. `python -m py_compile inference/main.py` passed.
4. `dotnet test BabelPlayer.Tests\BabelPlayer.Tests.csproj --no-build` passed with 389 tests.
5. Focused regression coverage now includes:
   - CTranslate2 provider artifact writing and single-segment regeneration
   - translation registry exposure for the new local lightweight provider
   - managed host compute-type policy staying on `float16` for shared GPU reliability
   - containerized/provider readiness checks for translation capability gating

## What Was Not Verified
- Real managed-host NLLB translation on CUDA hardware after the float16 policy change.
- First-run CTranslate2 model preparation on a clean machine with no existing Hugging Face cache.
- End-to-end app interaction selecting CTranslate2 in settings and generating translated output through the shell.

## Evidence
- GPU translation readiness and routing:
  - `Services/ManagedHostComputeTypePolicy.cs`
  - `Services/SessionWorkflowCoordinator.Containerized.cs`
  - `Services/SessionWorkflowCoordinator.cs`
  - `inference/main.py`
- Lightweight local provider and model preparation:
  - `Services/CTranslate2TranslationProvider.cs`
  - `Services/ModelDownloader.cs`
  - `Services/Registries/TranslationRegistry.cs`
  - `Models/ProviderNames.cs`
- UI/model surfaces:
  - `ViewModels/EmbeddedPlaybackViewModel.cs`
  - `ViewModels/ModelsTabViewModel.cs`
- Regression coverage:
  - `BabelPlayer.Tests/CTranslate2TranslationProviderTests.cs`
  - `BabelPlayer.Tests/RegistryTests.cs`
  - `BabelPlayer.Tests/ManagedHostComputeTypePolicyTests.cs`

## Notes
This pass keeps the existing CPU/GPU NLLB split intact and adds CTranslate2 as a separate local lightweight provider instead of silently replacing NLLB. The managed shared GPU host now favors truthful `float16` launch behavior over speculative `float8` routing so translation and XTTS readiness stay aligned with what the runtime can actually execute.

## Conclusion
Status: `partial`.

The GPU translation readiness mismatch is fixed at the policy and capability-reporting layer, and the new lightweight CTranslate2 local translation path is implemented and regression-tested. Real hardware/app-shell smoke is still required before this area should be considered fully verified.

## Deferred Items
- Run a manual managed-host GPU translation pass on a CUDA machine and confirm the capability log line plus successful translation output.
- Run a manual CTranslate2 first-install flow from the Models tab on a clean machine.
- Decide later whether CTranslate2 GPU acceleration is worth exposing after CPU validation is stable.
