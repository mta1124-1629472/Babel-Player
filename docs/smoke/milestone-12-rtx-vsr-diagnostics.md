---
milestone: 12
title: RTX VSR Diagnostics and UI Projection
status: partial
date: 2026-04-03
---

## Metadata
- Scope: structured RTX VSR diagnostics in the embedded mpv transport, coordinator-owned diagnostic projection, settings diagnostics surface, playback status badge, focused VSR tests
- Status: `partial`
- Operator: Codex

## Gate Summary
- [x] RTX VSR filter evaluation emits structured diagnostic snapshots instead of only raw warning strings.
- [x] Diagnostic snapshots capture requested state, resolved plan, reason, filter chain, geometry, backend result, and runtime context.
- [x] Coordinator projects the latest VSR diagnostic together with coarse hardware/settings hints.
- [x] Settings UI shows a detailed read-only VSR diagnostics block near the RTX controls.
- [x] Playback UI shows a compact VSR active/skipped/rejected status badge.
- [x] Structured VSR transport/coordinator/viewmodel tests were added.
- [x] Build passes.
- [x] Automated tests pass.
- [ ] Manual RTX playback verification on real NVIDIA hardware.

## What Was Verified
1. `python scripts/check-architecture.py` passed all checks.
2. `dotnet build BabelPlayer.csproj --no-restore` completed successfully.
3. `dotnet build BabelPlayer.Tests\BabelPlayer.Tests.csproj --no-restore /p:BuildProjectReferences=false` completed successfully.
4. `dotnet test BabelPlayer.Tests\BabelPlayer.Tests.csproj --no-build --filter FullyQualifiedName~VsrDiagnosticsTests|FullyQualifiedName~PipelineStageProgressTests` passed with 7 tests.
5. Focused coverage now verifies:
   - skipped VSR diagnostics for `no-upscaling-required`
   - rejected VSR diagnostics for libmpv result `-12`
   - coordinator/viewmodel projection of the latest VSR diagnostic into playback/settings surfaces

## What Was Not Verified
- Real playback on an RTX-capable Windows machine with `gpu-next` and VSR enabled.
- A successful `VSR active` path where libmpv accepts the `d3d11vpp:scaling-mode=nvidia` command on real hardware.
- The current rejection repro end to end in the desktop UI after rebuilding and launching the app.

## Evidence
- Transport diagnostics:
  - `Services/LibMpvEmbeddedTransport.cs`
  - `Services/VideoEnhancementDiagnostics.cs`
- Coordinator projection:
  - `Services/SessionWorkflowCoordinator.cs`
  - `Services/SessionWorkflowCoordinator.Playback.cs`
  - `Services/SessionWorkflowCoordinator.Settings.cs`
  - `Services/SessionWorkflowCoordinator.VideoDiagnostics.cs`
- UI surfaces:
  - `ViewModels/EmbeddedPlaybackViewModel.cs`
  - `ViewModels/SettingsViewModel.cs`
  - `Views/MainWindow.axaml`
  - `Views/SettingsWindow.axaml`
  - `Views/SettingsWindow.axaml.cs`
- Tests:
  - `BabelPlayer.Tests/VsrDiagnosticsTests.cs`

## Notes
This pass is diagnosis-first. It does not change the current enablement policy, support gating, or fallback behavior for RTX VSR. The goal is to make the existing behavior explainable in logs and in-app UI.

The transport now emits one structured diagnostic snapshot per distinct VSR state transition and includes the raw libmpv result plus a mapped human-readable backend label. The coordinator merges that runtime evidence with the coarse hardware hint so the UI can say what was requested, what happened, and why.

## Conclusion
Status: `partial`.

The code now exposes actionable RTX VSR diagnostics in logs, Settings, and playback UI. Real hardware smoke is still required before claiming the VSR path itself is working or before drawing conclusions about specific driver/backend combinations.

## Deferred Items
- Rebuild and launch the desktop app, then verify the playback badge and settings diagnostics update during a real VSR attempt.
- Reproduce the current `-12` rejection on a real RTX system and confirm the detailed reason surfaces without needing log inspection.
- Capture at least one successful `VSR active` repro on supported hardware and record the exact backend/runtime context in a follow-up smoke note.
