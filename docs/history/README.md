# History

## `smoke/`

Milestone smoke notes: manual gate evidence and status (`complete` / `partial` / `failed`). New notes for milestone work should be added here using the naming pattern `milestone-NN-short-label.md` (see [CONTRIBUTING.md](../../CONTRIBUTING.md)).

Status conventions for historical notes:
- `partial` often means implementation landed but manual/hardware validation remained outstanding at capture time.
- `historical-retired` means the note is preserved for context while the referenced feature/path is no longer active.
- Canonical current implementation state lives in `docs/Engineering-Plan.md`; smoke notes are timeline evidence snapshots.

## `benchmarks/`

CPU transcription benchmark JSON artifacts and the auto-generated [LEADERBOARD.md](benchmarks/LEADERBOARD.md). New result files go here; `scripts/aggregate_leaderboard.py` refreshes the leaderboard.
