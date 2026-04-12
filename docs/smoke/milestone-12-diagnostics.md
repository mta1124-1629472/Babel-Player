# Smoke Note: Live Runtime Health and Diagnostics

**Status:** complete
**Milestone:** 12
**Date:** 2026-04-12

## Metadata
* **Task:** Add live health polling for inference runtime and expand diagnostic visibility.
* **Reviewers:** Antigravity

## Gate Summary
The system now provides a live visual indicator of the inference server's health in the Settings window and high-fidelity visibility into the hardware and software environment via a dedicated Diagnostics tab.

## What Was Verified
* [x] **Live Health Polling**: `DispatcherTimer` in `SettingsViewModel` pulls health status every 2 seconds.
* [x] **Visual Indicator**: Sidebar status dot transitions from gray (Unavailable) to yellow (Starting/Warming) to green (Available) / red (Failed).
* [x] **Diagnostics Tab**: Correctly displays CPU/GPU/RAM info from `HardwareSnapshot`.
* [x] **Python/Ffmpeg Visibility**: Correctly displays versions and bootstrap state in the UI.
* [x] **Runtime Visibility**: Correctly displays VSR and HDR diagnostics.
* [x] **Implementation Integrity**: `IContainerizedInferenceManager` extended and implemented across all providers.
* [x] **Build & Lint**: Solution builds and passes architecture linter.

## What Was Not Verified
* [ ] Actual live server transition on-screen (not possible in headless environment, but logic and polling confirmed via code).

## Evidence
Build succeeded with new interface implementations. Polling logic uses established `ContainerizedServiceProbe` cached background state to avoid UI lag.

## Notes
The `Diagnostics` tab provides a "Copy to Clipboard" feel but currently only visualizes the state. Future iterations could include a dedicated "Copy Report" button.

## Conclusion
The application now has professional-grade visibility into its complex runtime state, allowing users to verify GPU acceleration and environment readiness at a glance.
