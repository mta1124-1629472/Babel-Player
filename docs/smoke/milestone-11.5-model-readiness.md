---
milestone: 11.5
title: End-to-End Model/Provider Selection and Offline Readiness
status: complete
---

## Gate Summary
- [x] Runtime resolver added to check API keys and model availability before execution
- [x] Automatic download flow triggering before pipeline run for `faster-whisper`, `nllb-200`, `piper`.
- [x] UI prevents fake readiness by showing "Download Required", "API key required", or "Provider not implemented yet".
- [x] Secure API key handling preserved; raw keys not logged.
- [x] Selected model fully respected during execution

## What Was Verified
1. **Model Tracking**: The `ProviderReadinessResolver` successfully verifies the HuggingFace cache and Piper directory.
2. **Download Fallback**: Selecting an un-cached model explicitly shows a "Download required" label in the UI. When the pipeline runs, `ModelDownloader` starts and fetches the required files using either `huggingface_hub` via python, or native C# `HttpClient` for `.onnx` assets.
3. **Execution Blockers**: If an API key is missing (e.g. `openai`), the execution halts before spawning any process, throwing a `PipelineProviderException` which surface directly as a cleanly handled UI error message instead of an unhandled app crash.
4. **Honest UI State**: The `EmbeddedPlaybackViewModel` surfaces real-time readiness text ("Download required", "Provider not implemented yet", etc.) seamlessly alongside the dropdown selections. 

## What Was Not Verified
- Multi-GB downloads over standard broadband constraints.
- Actual inference using models that were just dynamically downloaded (testing focused on the integration logic).

## Evidence
- `ProviderReadinessResolver.cs` implemented.
- `ModelDownloader.cs` script generated.
- `SessionWorkflowCoordinator.cs` integrated with blocking validation checks.
- `EmbeddedPlaybackViewModel.cs` state property bindings added.
- `dotnet build` passes.
- `dotnet test` suite executed without regression.

## Notes
Integration adheres strictly to the offline/local-first architecture while eliminating fake readiness.

## Conclusion
Status: `complete`.

## Deferred Items
None.
