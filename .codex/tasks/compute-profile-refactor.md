# Task: Low-Friction GPU Inference and Public `CPU / GPU / Cloud` UX

## Context

The current public UI exposes `Local`, `Containerized`, and `Cloud` as top-level runtime choices. These are host mechanics, not user-meaningful compute targets. This refactor replaces them with per-stage **compute profiles** (`CPU`, `GPU`, `Cloud`) that map onto the real implementation details internally, without exposing Docker vs. subprocess distinctions to users.

The managed local Python inference host (`ManagedInferenceHostManager`) becomes the default GPU path. Docker stays available only as an advanced GPU backend. GPU TTS is intentionally deferred until XTTS/local GPU TTS is fully implemented.

---

## Required Changes

### Step 1 — New enums

Add to the `BabelPlayer.Models` namespace (or wherever `InferenceRuntime` lives):

```csharp
public enum ComputeProfile { Cpu, Gpu, Cloud }
public enum GpuHostBackend { ManagedVenv, ContainerizedService }
```

`GpuHostBackend` defaults to `ManagedVenv`.

---

### Step 2 — Settings migration

In `AppSettings` (or the settings persistence class), add new persisted fields:

```csharp
public ComputeProfile TranscriptionProfile { get; set; } = ComputeProfile.Cloud;
public ComputeProfile TranslationProfile   { get; set; } = ComputeProfile.Cloud;
public ComputeProfile TtsProfile           { get; set; } = ComputeProfile.Cloud;
public GpuHostBackend PreferredLocalGpuBackend { get; set; } = GpuHostBackend.ManagedVenv;
public bool AlwaysStartLocalGpuRuntimeAtAppStart { get; set; } = false;
public string AdvancedGpuServiceUrl { get; set; } = "http://127.0.0.1:18000";
```

Migration rule (run once on load if the new fields are at their defaults but old `InferenceRuntime` fields are populated):

| Old value | New `ComputeProfile` |
|---|---|
| `Local` | `Cpu` |
| `Containerized` | `Gpu` |
| `Cloud` | `Cloud` |

Old `InferenceRuntime` fields become **load-only** after migration. Stop writing them. They may be removed in a future cycle.

---

### Step 3 — `ManagedInferenceHostManager`

Create `Services/ManagedInferenceHostManager.cs`.

Responsibilities:
- Bootstrap a per-user managed Python runtime at `%LOCALAPPDATA%\BabelPlayer\runtime\managed-gpu`
- Create and maintain a versioned `.venv` using a bundled `uv.exe`
- Sync pinned GPU dependencies from:
  - `inference/gpu-requirements.txt`
  - `inference/gpu-constraints.txt`
- Launch `inference/main.py` as a local HTTP inference host on `http://127.0.0.1:18000`
- Deduplicate concurrent start/install requests
- Expose a state machine:

```csharp
public enum ManagedHostState
{
    NotInstalled,
    Installing,
    Starting,
    Ready,
    Failed
}
```

Bootstrap sequence:
1. Check if managed venv exists and `inference/main.py` is present
2. If not: use bundled `uv.exe` to install Python 3.11 + sync requirements
3. Launch `inference/main.py` via managed Python
4. Poll `/health/live` until ready or timeout
5. Expose `ManagedHostState` and a `FailureReason` string

`DependencyLocator.FindPython()` must prefer the managed Python interpreter once the managed runtime has been bootstrapped, before falling back to app-local `python.exe` or `PATH`.

---

### Step 4 — `inference/main.py` GPU contract

Update `inference/main.py` to serve as the local GPU host with an honest capability contract:

**Required endpoints:**

```
GET  /health/live
GET  /capabilities
POST /transcribe
POST /translate
POST /tts          (returns { "ready": false } until XTTS/GPU TTS is implemented)
```

**`/capabilities` response contract:**

```json
{
  "transcription": { "ready": true,  "provider": "faster-whisper", "cuda": true },
  "translation":   { "ready": true,  "provider": "nllb-200",       "cuda": true },
  "tts":           { "ready": false, "reason": "not-implemented" }
}
```

`cuda` must reflect real CUDA availability detected at runtime, not assumed.

Remove non-GPU stubs (`googletrans`, `edge-tts`) from the GPU host. Those belong in CPU/Cloud paths only.

---

### Step 5 — Per-stage provider filtering

Update provider registries to be profile-aware. Each registry's `GetAvailableProviders()` (or equivalent) should accept `ComputeProfile` and return only providers valid for that profile:

**Transcription:**

| Profile | Providers |
|---|---|
| `Cpu` | `faster-whisper` |
| `Gpu` | `faster-whisper` |
| `Cloud` | `openai-whisper-api`, `google-stt`, `gemini` |

**Translation:**

| Profile | Providers |
|---|---|
| `Cpu` | `nllb-200` |
| `Gpu` | `nllb-200` |
| `Cloud` | `google-translate-free`, `deepl`, `openai`, `gemini` |

**TTS:**

| Profile | Providers |
|---|---|
| `Cpu` | `piper` |
| `Gpu` | *(hidden — not exposed until XTTS/GPU TTS is implemented)* |
| `Cloud` | `edge-tts`, `elevenlabs`, `google-cloud-tts`, `openai-tts` |

Do **not** show a public GPU TTS option until it is real and smoke-tested.

---

### Step 6 — Probe and readiness generalization

Rename and generalize the container-specific probing layer:

- `ContainerizedServiceProbe` → `InferenceHostProbe` (generic loopback HTTP probe)
- `ContainerizedProviderReadiness` → `HostBackedProviderReadiness`

Both `ManagedVenv` and `ContainerizedService` GPU backends reuse the same probe interface since they both expose a loopback HTTP inference host.

The managed host URL is `http://127.0.0.1:18000`. The Docker advanced URL is user-configured via `AdvancedGpuServiceUrl`.

---

### Step 7 — UI label and settings changes

**Settings panel changes:**

- Replace per-stage `Runtime` dropdowns with `Compute` selectors showing `CPU / GPU / Cloud`
- Under **Advanced GPU Settings** (collapsed by default):
  - `Preferred local GPU backend`: `Managed local GPU (Recommended)` | `Docker GPU service (Advanced)`
  - `Always start local GPU runtime at app start` (checkbox)
  - `Advanced GPU service URL` (text field, shown only when Docker backend is selected)

**Diagnostics / bootstrap log:**

Update `BootstrapDiagnostics` output to display compute values like:
- `CPU (Local subprocess)`
- `GPU (Managed local)`
- `GPU (Docker advanced)`
- `Cloud`

Stop using the word `Containerized` in any public-facing label.

**No silent fallback.** If `GPU` is selected and the runtime is not available, block with an honest remediation message. Never silently fall back to `CPU` or `Cloud`.

---

## Interface Summary

| Item | Type | Action |
|---|---|---|
| `ComputeProfile` | new enum | Add |
| `GpuHostBackend` | new enum | Add |
| `TranscriptionProfile` | settings field | Add |
| `TranslationProfile` | settings field | Add |
| `TtsProfile` | settings field | Add |
| `PreferredLocalGpuBackend` | settings field | Add |
| `AlwaysStartLocalGpuRuntimeAtAppStart` | settings field | Add |
| `AdvancedGpuServiceUrl` | settings field | Add |
| `ManagedInferenceHostManager` | new service | Add |
| `DependencyLocator.FindPython()` | existing method | Update to prefer managed runtime |
| `ContainerizedServiceProbe` | existing class | Rename/generalize to `InferenceHostProbe` |
| `ContainerizedProviderReadiness` | existing class | Rename/generalize to `HostBackedProviderReadiness` |
| `InferenceRuntime` fields | old settings fields | Load-only after migration, do not write |

---

## Test Plan

### Unit / integration tests to add or update

- Settings migration: `Local / Containerized / Cloud` → `Cpu / Gpu / Cloud` round-trips correctly
- `ManagedInferenceHostManager`: bootstrap command generation, deduplication, versioning, failure handling
- `DependencyLocator.FindPython()` returns managed Python path after bootstrap, falls back to app-local then `PATH`
- Provider filtering per profile per stage returns correct sets
- GPU profile uses `ManagedVenv` by default; uses `ContainerizedService` only when explicitly selected
- `InferenceHostProbe` readiness:
  - GPU transcription: `ready` when managed host is healthy
  - GPU translation: `ready` when managed host is healthy
  - GPU TTS: always `not-ready` until implementation gate is lifted
- No silent fallback: selecting `GPU` when CUDA is unavailable returns an explicit not-ready state, not a downgrade
- Existing Docker autostart still works when `ContainerizedService` backend is explicitly selected

### Manual smoke tests

1. **Clean Windows NVIDIA machine, no Python, no Docker:**
   - Select `GPU` for transcription and translation
   - Managed runtime bootstraps from bundled `uv.exe`
   - Transcription completes successfully
   - Translation completes successfully

2. **Machine with Docker selected as advanced GPU backend:**
   - Existing container autostart still fires
   - Workflow completes

3. **Machine without CUDA:**
   - Selecting `GPU` shows an honest not-ready state with remediation text
   - Selecting `CPU` still works

---

## Assumptions and Deferred Items

- First managed GPU rollout targets **Windows + NVIDIA CUDA only**
- WSL is not part of the primary user-facing GPU path in this cycle
- Docker remains supported as an advanced backend
- GPU TTS (`GPU` profile for TTS stage) is **intentionally deferred** until XTTS/local GPU TTS is implemented end-to-end
- CPU path remains subprocess-based but may transparently reuse the managed Python install once it is present
- `uv.exe` must be bundled with the app before this feature ships
