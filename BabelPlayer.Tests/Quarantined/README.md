# Quarantined Tests

These tests are intentionally kept on disk but excluded from compile.

They are legacy coverage for slow, flaky, runtime-heavy, or orchestration-heavy scenarios that are not part of the maintained day-to-day suite. Do not move tests back into the compiled project unless they are rewritten to satisfy `docs/testing-requirements.md`.

Agents: do not add new broad coordinator, orchestrator, preview-player, or UI workflow suites to the compiled `BabelPlayer.Tests` project. Those belong here or should be deleted.
