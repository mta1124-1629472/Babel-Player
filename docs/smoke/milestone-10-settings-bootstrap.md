# Smoke Note — Milestone 10: settings-bootstrap

## Metadata

- **Date:** 2026-03-31
- **Status:** partial
- **Branch:** feature/hardware-video-decode
- **Build:** dotnet build → 0 errors, 0 warnings
- **Tests:** dotnet test → 56 passed, 0 failed

## Gate Summary

Milestone 10 extends settings and bootstrap to cover hardware video decode and GPU API
selection. A new `VideoPlaybackOptions` record threads hwdec and gpu-api choices from
`AppSettings` through `MediaTransportManager` into `LibMpvEmbeddedTransport`, where they
are applied via `SetOption()` before `mpv_initialize()`. A `SettingsWindow` Video tab
exposes three ComboBoxes (hwdec, gpu-api, export encoder) and a restart-required notice.
`HardwareEncoderHelper` is added as a dormant resolver for a future export stage. The gate
is partially met: the plumbing and UI are complete and all tests pass, but manual playback
with non-default hwdec values and export encoder resolution remain unverified because the
export stage does not yet exist.

## What Was Verified

- `Models/VideoPlaybackOptions.cs` — new record captures hwdec and gpu-api as init-time options
- `Services/Settings/AppSettings.cs` — `VideoHwdec`, `VideoGpuApi`, and `VideoExportEncoder` properties added, all defaulting to `"auto"`
- `Services/LibMpvEmbeddedTransport.cs` — constructor accepts `VideoPlaybackOptions` and applies hwdec and gpu-api via `SetOption()` before `mpv_initialize()`
- `Services/MediaTransportManager.cs` — stores `VideoPlaybackOptions` and passes it to `LibMpvEmbeddedTransport` at lazy-create time
- `Services/SessionWorkflowCoordinator.cs` — constructs `VideoPlaybackOptions(settings.VideoHwdec, settings.VideoGpuApi)` and passes it to `MediaTransportManager`
- `Services/HardwareEncoderHelper.cs` — new dormant resolver; selects h264_nvenc / h264_amf / h264_qsv / libx264 based on `HardwareSnapshot` (no call site active yet)
- `ViewModels/SettingsViewModel.cs` — exposes `VideoHwdec`, `VideoGpuApi`, `VideoExportEncoder` as observable properties with `HwdecOptions`, `GpuApiOptions`, and `ExportEncoderOptions` arrays
- `Views/SettingsWindow.axaml` — Video tab contains three ComboBoxes and a restart-required notice
- `SettingsViewModel.Apply()` — saves all three video settings through the coordinator to `AppSettings`
- Build: `dotnet build` → 0 errors, 0 warnings
- Tests: `dotnet test` → 56 passed, 0 failed

## What Was Not Verified

- Manual playback with explicit hwdec values (`d3d11va`, `nvdec`, `qsv`) — no manual run performed with non-default selections; functional correctness of SetOption paths for specific decoders is unconfirmed
- Export encoder resolution — `HardwareEncoderHelper` is dormant; no export stage exists, so the encoder selection logic has no exercised call site

## Evidence

Build output (static analysis only — no interactive session):

```
dotnet build BabelPlayer.csproj
  0 Error(s)
  0 Warning(s)
```

Test run:

```
dotnet test
  56 passed, 0 failed
```

Code-level evidence:

- `LibMpvEmbeddedTransport` constructor applies `SetOption("hwdec", options.HwdecMode)` and
  `SetOption("gpu-api", options.GpuApi)` before the `mpv_initialize()` call, matching the
  libmpv requirement that these options be set at init time.
- `MediaTransportManager` holds a single `VideoPlaybackOptions` instance and passes it on
  first construction of `LibMpvEmbeddedTransport`; the transport is lazy-created, so
  settings are always applied before the first `mpv_initialize()` call.
- `SettingsViewModel.Apply()` writes all three video settings to `AppSettings` and then
  calls through the coordinator so the settings service persists them to disk immediately.
- `HardwareEncoderHelper.ResolveEncoder(AppSettings settings, HardwareSnapshot hw)` returns
  the first available hardware encoder or falls back to `libx264`; method is defined but
  has no active call site.

## Notes

- The restart-required notice in the Settings Video tab is correct and intentional:
  libmpv options set before `mpv_initialize()` cannot be changed at runtime without
  destroying and recreating the transport. The current architecture does not support
  hot-reload of these options.
- `HardwareEncoderHelper` is intentionally dormant. It was introduced here to collocate
  hardware capability logic with the other hardware settings work, not to enable export.
  It must not be wired up until the export stage milestone is active.
- The existing M10 gate evidence (settings persistence, session restore, bootstrap
  diagnostics, API key storage, hardware snapshot) remains valid under the earlier
  `milestone-10-settings-bootstrap.md` entry; this note covers only the hardware video
  decode additions landed in `feature/hardware-video-decode`.

## Conclusion

Gate partially met: the hardware video decode settings plumbing, UI, and test coverage are
complete, but manual verification of non-default hwdec values and the export encoder path
remain deferred.

## Deferred Items

- Manual playback with d3d11va, nvdec, and qsv hwdec values (deferred — no interactive test session; verify before merging to main)
- Export encoder resolution via `HardwareEncoderHelper` (deferred — export stage does not exist yet; revisit at the export milestone)
