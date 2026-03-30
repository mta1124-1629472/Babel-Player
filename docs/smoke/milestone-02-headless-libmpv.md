# Milestone 02 Headless libmpv - Smoke Note

## Metadata
- Milestone: `02`
- Name: `Headless libmpv`
- Date: `2026-03-28`
- Status: `complete`

## Gate Summary
- [x] Repeated headless playback cycles complete without hangs
- [x] Teardown is stable
- [x] No ghost state survives reload cycles
- [x] A smoke note confirms repeated success rather than one lucky run

## What Was Verified
- Application builds and runs.
- `libmpv-2.dll` loads successfully from `native/win-x64`.
- libmpv initializes in headless mode with `vo=null` and `ao=null`.
- Media file loads successfully.
- Duration property can be read.
- Play/pause works reliably when called after media is ready.
- Seek functionality works.
- HasEnded property returns the expected state in the verified test path (false immediately after load).
- Dispose works cleanly.
- Multiple load/unload cycles complete without hangs.
- Core headless lifecycle behavior has passing coverage for initialize, load plus duration, dispose, and repeated load/unload cycles.

## What Was Not Verified
- Explicit ended/completed event (playback-to-completion) verification is not explicitly tested - relies on HasEnded property polling.
- Full event-loop integration is not implemented - using polling approach for readiness.

## Evidence

### Commands Run
```text
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj
```

### Test Results
```text
Total tests: 7
Passed: 7
- Initialize
- Load+Duration
- Play/Pause
- Dispose
- Repeated Load/Unload Cycles
- Seek
- HasEnded State
```

### Artifacts / Paths
- Native DLL: `native/win-x64/libmpv-2.dll`
- Test media: `test-assets/video/sample.mp4`

## Notes
- `IMediaTransport` defines `Load`, `Play`, `Pause`, `Seek`, `CurrentTime`, `Duration`, `HasEnded`, and `Dispose`.
- `LibMpvHeadlessTransport` is a P/Invoke wrapper around `libmpv-2.dll`.
- Play() method includes internal waiting logic to ensure media is ready before setting pause state.
- Seek test uses Play() first to ensure media is loaded/ready before seeking.

## Conclusion
Milestone 02 is complete. The core libmpv functionality has been verified:
- DLL loads and initializes in headless mode
- File loading works
- Duration can be read
- Play/pause is reliable
- Seek works
- HasEnded reports the expected non-ended state in the verified test path
- Clean disposal works
- Repeated load/unload cycles complete without hangs

## Deferred Items
- Event-based file-ready handling (polling approach currently works)
- Explicit playback-to-completion testing (property polling is sufficient for headless use)
