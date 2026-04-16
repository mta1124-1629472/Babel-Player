## Learned User Preferences
- Prefer iterative runtime debugging loops: reproduce issue, then continue from fresh logs.
- Prefer non-hardcoded debug log paths for instrumentation; use env var override with repo-root fallback.
- For continual-learning runs, require strict incremental transcript processing and high-signal-only memory updates.
- Prefer disabling/removing Cursor hooks that interrupt normal workflow (popups or cursor focus changes).
- Prefer canonical language codes in persisted settings and pipeline artifacts (typically lowercase ISO 639-1); keep human-readable labels only in UI catalogs to avoid provider contract and strict string-compare mismatches.
- Prefer custom window chrome that matches Windows 11 expectations: flush edge-to-edge segments, square full hit targets, and even alignment without ragged gaps between adjacent controls.
- For NVIDIA RTX Video, bias defaults toward enabling VSR and RTX HDR when hardware and gates allow; keep RTX HDR and HDR passthrough mutually exclusive in the UI; when RTX HDR is enabled, suppress or disable conflicting secondary HDR processing options (such as tone mapping) that should apply under passthrough instead.
- Prefer Piper and Edge TTS voice download and per-speaker voice assignment through the Speaker Reference Wizard rather than duplicating long voice lists in Settings or the main pipeline controls.
- For commit/push requests that provide an explicit staged/unstaged file list, treat that list as authoritative and do not include files outside it.
- When asked to implement from an attached plan with pre-created todos, execute the plan without editing the plan file and progress existing todos instead of creating duplicates.

## Learned Workspace Facts
- This workspace uses a project transcript store under the standard Cursor project transcripts location.
- The codebase actively uses both managed CPU and managed GPU inference flows for diarization/transcription debugging.
- The repository no longer uses Git LFS; LFS-related hook assumptions are outdated here.
- Docker support is maintained as a power-user inference-host option; containerizing the desktop app is not the primary runtime model.
- Forward-facing product naming uses `Babel Player` (space, no dash); dev builds append `[DEV]`.
- On Windows, client diagnostics are commonly written to `%LocalAppData%/BabelPlayer/logs/babel-player.log`.
- Per-provider language allowlists and multilingual capability tags are maintained in centralized catalog types in the codebase rather than ad hoc string checks scattered through the pipeline.
- NVIDIA RTX Video features (VSR, RTX HDR) are gated on supported GPU hardware, display HDR state where applicable, and the GPU-accelerated video path (for example `VideoUseGpuNext`-style settings), not on a single flag alone.
- Public project site/docs are served via GitHub Pages at `https://babelworks.github.io/Babel-Player/`.
