## Learned User Preferences
- Prefer iterative runtime debugging loops: reproduce issue, then continue from fresh logs.
- Prefer non-hardcoded debug log paths for instrumentation; use env var override with repo-root fallback.
- For continual-learning runs, require strict incremental transcript processing and high-signal-only memory updates.

## Learned Workspace Facts
- This workspace uses a project transcript store under the standard Cursor project transcripts location.
- The codebase actively uses both managed CPU and managed GPU inference flows for diarization/transcription debugging.
- The incremental continual-learning index for this repo is tracked at .cursor/hooks/state/continual-learning-index.json.
- The repository no longer uses Git LFS; LFS-related hook assumptions are outdated here.
