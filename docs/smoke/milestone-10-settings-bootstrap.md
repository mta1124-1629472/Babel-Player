# Milestone 10: Settings and Bootstrap — Smoke Note

## Metadata

| Field | Value |
|---|---|
| Milestone | 10 |
| Name | Settings and Bootstrap |
| Date | 2026-03-31 |
| Status | **complete** |

---

## Gate Summary

| Gate item | Status |
|---|---|
| Restart persistence works | ✅ |
| Recent sessions reopen correctly | ✅ |
| Missing configuration is explicit and recoverable | ✅ |
| Setup states do not pretend readiness | ✅ |

---

## What Was Verified

### Settings persistence

- `AppSettings` (transcription provider/model, translation provider/model, TTS provider/voice, target language, theme, max recent sessions, auto-save) serialises to `{LocalAppData}/BabelPlayer/settings/app-settings.json` as indented JSON.
- `SettingsService.LoadOrDefault()` returns clean defaults when the file is absent, empty, or corrupt — logs a warning, never throws.
- `SettingsService.Save()` failures are logged but non-fatal to the application.
- Provider/model changes in the left config panel fire `SessionWorkflowCoordinator.SettingsModified`, which triggers `SettingsService.Save()` from `MainWindowViewModel` — settings are persisted on change, not only on exit.
- Verified round-trip: changing TTS voice or transcription model, restarting, and confirming the selection is restored.

### Session persistence and recent sessions

- `SessionSnapshotStore` persists the current workflow session (`WorkflowSessionSnapshot`) to `{LocalAppData}/BabelPlayer/state/current-session.json`.
- `PerSessionSnapshotStore` caches per-media session snapshots, enabling MRU session resumption across different source files.
- `RecentSessionsStore` maintains a capped list (default 10) of recent sessions, surfaced in the UI as a Recent Sessions drop-down.
- On restart: previously loaded media, completed pipeline stages, transcript/translation/TTS artifact paths, and `TtsSegmentAudioPaths` dictionary are all restored.
- Missing or corrupt session state is backed up with a timestamp suffix and a fresh session starts — no crash, no silent corruption.

### Bootstrap diagnostics

- `BootstrapDiagnostics.Run()` probes for Python and ffmpeg at startup using `DependencyLocator` (checks bundled paths first, then PATH).
- If Python or ffmpeg is missing, the coordinator exposes the diagnostic via `BootstrapDiagnostics` property; the pipeline VM surfaces a banner warning in the UI and aborts `RunPipelineCommand` before attempting inference.
- The app does not pretend the pipeline is ready when dependencies are absent.

### API key storage

- `ApiKeyStore` stores provider API keys (OpenAI, Google AI, ElevenLabs, DeepL) encrypted via Windows DPAPI; Base64 fallback on non-Windows with an inline comment that it is not cryptographic.
- The `🔑 API Keys` dialog allows entering, revealing, and clearing keys per provider; status dot is green when a key is saved, grey when absent.
- Key presence is checked by `ProviderCapability` before cloud-backed pipeline stages run — a missing key surfaces a `PipelineProviderException` rather than silently failing mid-execution.
- Keys are never logged.

### Hardware snapshot

- `HardwareSnapshot.Run()` is called on a background `Task` at startup; the result is posted to the UI thread and bound to the left-panel hardware footer.
- Displayed: CPU name, core count, AVX2/AVX-512 flags, system RAM, GPU name + VRAM (via nvidia-smi), CUDA version, OpenVINO version (via Python probe), NPU label (heuristic from CPU name).
- Individual detection failures silently produce null/false — the app never crashes on hardware detection failure.
- Shows `—` for unavailable or undetected values rather than hiding the row.

### Settings window

- `SettingsWindow` (modal) covers: target language, TTS voice, theme (Light/Dark/System), max recent sessions, auto-save toggle.
- Settings applied via `SettingsViewModel` write through to coordinator `CurrentSettings` and trigger `SettingsService.Save()`.

---

## What Was Not Verified

- Theme switching (Light/Dark/System) — `AppSettings.Theme` is persisted but Avalonia theme-switching is not wired in this milestone; field is preserved for M13.
- "Test connection" or key validation for cloud providers — keys can be stored but are not verified against live APIs here. That is intentional.
- Bundled Python/ffmpeg packaging — `DependencyLocator` supports bundled paths but the installer/packager is deferred to M13 (Release Hardening).
- Windows DPAPI key recovery on user account change — DPAPI behaviour on user re-provisioning is a platform detail outside this milestone's scope.

---

## Evidence

- `Services/Settings/AppSettings.cs` — 10 persisted fields with defaults
- `Services/Settings/SettingsService.cs` — safe load/save with fallback
- `Services/BootstrapDiagnostics.cs` — explicit Python/ffmpeg probe result
- `Services/DependencyLocator.cs` — bundled + PATH probe for Python and ffmpeg
- `Services/Credentials/ApiKeyStore.cs` — DPAPI-encrypted key storage
- `Services/HardwareSnapshot.cs` — background hardware detection record
- `ViewModels/EmbeddedPlaybackViewModel.cs` — surfaces `BootstrapDiagnostics`, `HardwareSnapshot`, key status per provider
- `Views/ApiKeysDialog.axaml` + `ViewModels/ApiKeysViewModel.cs` — API keys UI

### Build and test status

```
dotnet build BabelPlayer.csproj   → 0 errors, 0 warnings
dotnet test                       → 83 passed, 0 failed
```

---

## Deferred Items

- Theme switching wired to Avalonia `FluentTheme` — M13
- Bundled Python/ffmpeg included in published output — M13
- "Test API key" validation button in the API Keys dialog — future milestone
- DPAPI alternative for Linux (secret-service / keychain integration) — M13 or later
