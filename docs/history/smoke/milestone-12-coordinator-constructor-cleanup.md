# Metadata
- milestone: 12
- label: coordinator-constructor-cleanup
- date: 2026-04-13
- status: partial

# Gate Summary
- [x] `dotnet build Babel-Player.sln`
- [x] `dotnet test Babel-Player.sln`
- [x] `python scripts/check-architecture.py`
- [x] `python -m py_compile inference/main.py`
- [x] Manual app-shell smoke of coordinator construction through desktop startup

# What Was Verified
- `SessionWorkflowCoordinator` now takes a required `CoordinatorCoreServices` bundle for `SessionSnapshotStore`, `AppLog`, and `AppSettings`.
- The production constructor now exposes 4 parameters and the convenience overload exposes 5, satisfying the constructor cleanup gate without moving required registries or transport ownership into `CoordinatorOptions`.
- `DependencyLocator` now constructs `CoordinatorCoreServices`, so the canonical composition root matches the refactored coordinator surface.
- All test helpers and direct call sites construct the coordinator through the new required-services bundle.
- The engineering plan now records 6.4 as complete and moves the next remaining action to ViewModel decomposition.
- Manual confirmation was provided on 2026-04-15 that desktop startup constructs the coordinator cleanly through the full app shell.

# What Was Not Verified
- Interactive media load / restore flow from the desktop UI after the constructor refactor.

# Evidence
- `dotnet build Babel-Player.sln` succeeded on 2026-04-13 with 0 errors and 0 warnings.
- `dotnet test Babel-Player.sln` passed: 898 passed, 0 failed, 0 skipped.
- Architecture linter passed all checks.
- `python -m py_compile inference/main.py` completed successfully.

# Notes
- This smoke note remains `partial` only for broader interactive load/restore flow follow-up; startup construction is now manually confirmed.
- An intermediate compile error in `SessionWorkflowTests` during the signature migration was fixed before the final verification run.

# Conclusion
- Constructor cleanup 6.4 is implemented and verified at build, test, and architecture levels.
- Tier 1 is closed for code purposes; the next remaining implementation action is 6.1b ViewModel decomposition.

# Deferred Items
- Begin Tier 1 remaining work by moving playback, subtitle, and dub-mode logic out of `EmbeddedPlaybackViewModel`.
