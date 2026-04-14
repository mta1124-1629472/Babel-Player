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
- [ ] Manual app-shell smoke of coordinator construction through desktop startup

# What Was Verified
- `SessionWorkflowCoordinator` now takes a required `CoordinatorCoreServices` bundle for `SessionSnapshotStore`, `AppLog`, and `AppSettings`.
- The production constructor now exposes 4 parameters and the convenience overload exposes 5, satisfying the constructor cleanup gate without moving required registries or transport ownership into `CoordinatorOptions`.
- `DependencyLocator` now constructs `CoordinatorCoreServices`, so the canonical composition root matches the refactored coordinator surface.
- All test helpers and direct call sites construct the coordinator through the new required-services bundle.
- The engineering plan now records 6.4 as complete and moves the next remaining action to ViewModel decomposition.

# What Was Not Verified
- Manual desktop startup confirming the Avalonia app constructs the coordinator cleanly from the full app shell after the signature change.
- Interactive media load / restore flow from the desktop UI after the constructor refactor.

# Evidence
- `dotnet build Babel-Player.sln` succeeded on 2026-04-13 with 0 errors and 0 warnings.
- `dotnet test Babel-Player.sln` passed: 898 passed, 0 failed, 0 skipped.
- Architecture linter passed all checks.
- `python -m py_compile inference/main.py` completed successfully.

# Notes
- This smoke note is `partial` because no manual desktop session was run from this environment after changing the composition path.
- An intermediate compile error in `SessionWorkflowTests` during the signature migration was fixed before the final verification run.

# Conclusion
- Constructor cleanup 6.4 is implemented and verified at build, test, and architecture levels.
- Tier 1 is closed for code purposes; the next remaining implementation action is 6.1b ViewModel decomposition.

# Deferred Items
- Run a manual app-shell startup smoke to confirm coordinator construction and session initialization through the desktop UI.
- Begin Tier 1 remaining work by moving playback, subtitle, and dub-mode logic out of `EmbeddedPlaybackViewModel`.
