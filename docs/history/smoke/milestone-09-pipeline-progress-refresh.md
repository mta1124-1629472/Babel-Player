# Milestone 09 Pipeline Progress Refresh

## Metadata
- Milestone: `09-pipeline-progress-refresh`
- Name: `Playback Controls Refresh and Verbose Stage Progress`
- Date: `2026-04-03`
- Status: `partial`

## Gate Summary
- [x] `Open Media` moved out of the left footer and into the bottom video controls row as a folder-only button
- [x] `Dub` label is stable and uses active styling instead of `On` / `Off` text
- [x] Dedicated verbose pipeline progress card added above `Run Pipeline`
- [x] Coordinator emits stage-aware progress updates with truthful remaining-stage counts
- [x] Real model-download percentages are mapped into the active stage bar when a provider reports them
- [ ] Manual desktop verification of the refreshed control layout and live progress behavior

## What Was Verified
- `Views/MainWindow.axaml` now removes the footer `Open Media` button, adds a folder button in the bottom controls row before `CC`, and binds the `Dub` button to active styling
- `ViewModels/EmbeddedPlaybackViewModel.cs` now exposes verbose stage-progress UI state (`PipelineStageTitle`, `PipelineStageDetail`, `PipelineProgressPercent`, `IsPipelineProgressVisible`, `IsPipelineProgressIndeterminate`) and keeps `DubModeLabel` stable as `🎙 Dub`
- `Services/SessionWorkflowCoordinator.Pipeline.cs` and `Services/SessionWorkflowCoordinator.Progress.cs` now compute truthful remaining-stage counts and emit per-stage progress updates for transcription, translation, and dubbing
- `SessionWorkflowCoordinator.cs` was reduced back under the architecture linter threshold by extracting the new pipeline execution logic into partial files
- `BabelPlayer.Tests/PipelineStageProgressTests.cs` verifies:
  - fresh pipeline run emits `Transcription -> Translation -> Dub` as `1/3`, `2/3`, `3/3`
  - translated-session resume emits only `Dub` as `1/1`
  - provider download progress maps into the active stage bar
  - the playback view-model keeps `🎙 Dub` constant and applies verbose progress-card state correctly
- `python scripts/check-architecture.py` passes
- `dotnet build BabelPlayer.csproj` passes
- `dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --filter PipelineStageProgressTests` passes with 4 tests

## What Was Not Verified
- Real interactive desktop smoke for:
  - folder button placement in the transport bar
  - `Dub` active/inactive accent styling in the running Avalonia shell
  - stage card visibility and live wording during a real pipeline run
  - post-TTS refresh text while segment data reloads
- Manual re-run from a partially completed real session to confirm the UI shows `1/1` or `1/2` remaining-stage counts correctly

## Evidence

### Commands Run
```bash
python scripts/check-architecture.py
dotnet build BabelPlayer.csproj
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --filter PipelineStageProgressTests
```

### Test Results
```text
Total tests: 4
Passed: 4
Failed: 0
Skipped: 0
```

### Modified Files
- `Services/SessionWorkflowCoordinator.cs`
- `Services/SessionWorkflowCoordinator.Pipeline.cs`
- `Services/SessionWorkflowCoordinator.Progress.cs`
- `ViewModels/EmbeddedPlaybackViewModel.cs`
- `Views/MainWindow.axaml`
- `BabelPlayer.Tests/PipelineStageProgressTests.cs`

## Notes
- The stage-progress payload remains an internal coordinator/view-model seam; public pipeline APIs keep their original external shape through wrapper overloads
- Determinate bar fill is only used when a provider exposes real percentage progress; opaque inference work remains indeterminate by design
- The progress card is intentionally reset after final status handling so completion, cancel, and failure messages still land in the existing status row

## Conclusion
The playback shell now has the requested control-bar layout and a substantially richer, truthful stage-progress model. Automated verification covers the coordinator sequencing and the view-model state updates, but the final UI behavior in the running desktop shell still needs a manual smoke pass, so this note remains `partial`.

## Deferred Items
- Manual Avalonia smoke of the folder-button placement, `Dub` active styling, and live progress-card behavior during a real pipeline run
- Optional future refinement: add real numeric TTS segment progress if a provider/runtime starts reporting segment-level percentages
