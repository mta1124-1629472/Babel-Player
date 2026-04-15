## Learned User Preferences
- Prefer iterative runtime debugging loops: reproduce issue, then continue from fresh logs.
- Prefer non-hardcoded debug log paths for instrumentation; use env var override with repo-root fallback.
- For continual-learning runs, require strict incremental transcript processing and high-signal-only memory updates.
- Prefer disabling/removing Cursor hooks that interrupt normal workflow (popups or cursor focus changes).
- Prefer canonical language codes in persisted settings and pipeline artifacts (typically lowercase ISO 639-1); keep human-readable labels only in UI catalogs to avoid provider contract and strict string-compare mismatches.

## Learned Workspace Facts
- This workspace uses a project transcript store under the standard Cursor project transcripts location.
- The codebase actively uses both managed CPU and managed GPU inference flows for diarization/transcription debugging.
- The repository no longer uses Git LFS; LFS-related hook assumptions are outdated here.
- Docker support is maintained as a power-user inference-host option; containerizing the desktop app is not the primary runtime model.
- Forward-facing product naming uses `Babel Player` (space, no dash); dev builds append `[DEV]`.
- On Windows, client diagnostics are commonly written to `%LocalAppData%/BabelPlayer/logs/babel-player.log`.
