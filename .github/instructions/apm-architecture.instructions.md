---
applyTo: "**/*.cs,**/*.axaml,inference/**/*.py"
---

# Babel Player — Architectural Rules for AI Agents

These rules are codified in `apm.yml` and enforced by `scripts/check-architecture.py`.
Violations will fail CI. Do not override them.

## Platform

- **Runtime:** .NET 10 / Avalonia 12 desktop application. Not a web app, not React, not MAUI.
- **Output:** `WinExe` targeting `win-x64` and `win-arm64`.
- **Root namespace:** `Babel.Player` / assembly `BabelPlayer`.

## Single State Owner

`SessionWorkflowCoordinator` (in `Babel.Player.Services`) is the **sole owner** of all
workflow and session state. Its logic is split across `SessionWorkflowCoordinator*.cs`
partial files but acts as one boundary.

**Never:**
- Put pipeline state in a ViewModel
- Call stage methods directly from Views or ViewModels
- Duplicate session state in a helper service

## Pipeline Stages and Entry Points

Stages advance in this order only:

```
Foundation → MediaLoaded → Transcribed → Diarized → Translated → TtsGenerated
```

Stage advancement must go through coordinator entry points:
- `AdvancePipelineAsync`
- `ContinuePipelineAsync`
- `RunTtsOnlyAsync`

## Service Placement

| Namespace | Contains |
|---|---|
| `Babel.Player.Services` | SessionWorkflowCoordinator and all orchestration |
| `Babel.Player.Services.Registries` | TranscriptionRegistry, TranslationRegistry, TtsRegistry |
| `Babel.Player.Services.Credentials` | API key storage and validation |
| `Babel.Player.Services.Settings` | Persisted user settings |
| `Babel.Player.Models` | Domain records and enums only — no behavior |
| `Babel.Player.ViewModels` | Display state and command wiring — no pipeline calls |
| `Babel.Player.Views` | Avalonia XAML UI — no logic |

## Provider Constants

All provider identifiers and credential keys must come from `Models/ProviderNames.cs`
(`ProviderNames.*`, `CredentialKeys.*`). The architecture linter **rejects inline
string literals** for provider names in production code.

## Compute Profile Normalization

Use `InferenceRuntimeCatalog` for all CPU/GPU/Cloud profile mapping and normalization.
Do not duplicate compute-selection logic in UI or service code.

## GPU Inference Pipeline

- GPU-local providers route through the **managed host or containerized path**.
  They must not be invoked directly from UI or ViewModel code.
- CPU-local providers use the **managed runtime bootstrap path** (uv-managed venv).
- If a compute path is unavailable, surface a **truthful blocked state with
  remediation**. No silent fallbacks. No fake-ready UI.

## Media Transport

| Transport | Purpose |
|---|---|
| `LibMpvEmbeddedTransport` | Source video/audio preview |
| `LibMpvHeadlessTransport` | Segment and TTS playback |

Both are created and owned through `MediaTransportManager`.

## Python / C# Serialization Contract

The Python/C# boundary is an **explicit contract**:
- Match JSON field names deliberately via `[JsonPropertyName]` or
  `PropertyNameCaseInsensitive = true` — do not rely on implicit PascalCase conversion.
- Segment IDs use the format `segment_{start}` (e.g. `segment_0.0`, `segment_3.68`).
  This format is stable — TTS artifacts are keyed by segment ID.
  **Do not change the segment ID format.**

## Persistence

| Store | Responsibility |
|---|---|
| `SessionSnapshotStore` | Current-session snapshot + corruption recovery |
| `PerSessionSnapshotStore` | Per-session artifacts under session ID directory |
| `RecentSessionsStore` | MRU session list |

On restore, validate artifacts and **downgrade the saved stage** if files are missing.
Never restore to a stage whose artifacts do not exist on disk.

## Scope Discipline

- Work targets the **current milestone** in `docs/PLAN.md`. Read it before starting.
- Milestone smoke notes live in `docs/history/smoke/`. Check them for gate evidence.
- No speculative abstractions, premature extension points, or "future-proof" refactors
  unless directly required by the current milestone.
- Fake readiness is forbidden. Use explicit placeholders or disabled states.
