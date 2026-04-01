---
name: Driver Runtime Compatibility Report
description: "Standardize hardware/driver/runtime diagnostics into a machine-readable JSON report for CI gates, support tickets, and inference-path recommendations."
argument-hint: "Provide environment details, provider and runtime stack, diagnostics outputs, and desired JSON schema strictness."
agent: "Hardware Acceleration Architect"
---
Use this prompt to convert mixed diagnostics into a normalized JSON artifact.

## Paste this template
"Generate a normalized hardware acceleration compatibility report in strict JSON.

Context:
- OS/version: <...>
- App version/commit: <...>
- CPU: <...>
- GPU(s): <...>
- NPU(s): <...>
- .NET runtime/SDK: <...>
- Python/runtime stack: <...>
- Providers configured: <cpu/cuda/directml/openvino/npu>
- Deployment mode: <native/docker/service>

Collected diagnostics (raw text/logs/command output):
<PASTE OUTPUTS>

Output requirements:
1) Return ONLY valid JSON (no markdown, no prose).
2) Include confidence scores per detection area and per provider.
3) Include explicit `selected_route_recommendation` and `fallback_route` by stage.
4) Include `blocking_issues` and `warnings` arrays.
5) Include `next_actions` array with ordered remediation steps.
6) Include `ci_gate` verdict (`pass`, `warn`, or `fail`).

Schema (required keys):
{
  "report_version": "1.0",
  "generated_at_utc": "<ISO-8601>",
  "environment_confidence": {
    "cpu_detection": 0.0,
    "gpu_detection": 0.0,
    "npu_detection": 0.0,
    "runtime_detection": 0.0
  },
  "environment": {
    "os": {"name": "", "version": ""},
    "cpu": {"model": "", "features": ["avx2","avx512","sse","neon"]},
    "gpus": [{"name": "", "driver_version": "", "vram_gb": 0, "visible": true}],
    "npus": [{"name": "", "driver_version": "", "visible": true}],
    "dotnet": {"sdk": "", "runtime": ""},
    "python": {"version": ""},
    "deployment_mode": {
      "value": "native",
      "allowed_values": ["native", "docker", "service"]
    }
  },
  "providers": [
    {
      "name": "cpu|cuda|directml|openvino|npu",
      "installed": true,
      "runtime_version": "",
      "device_visible": true,
      "compatibility": "supported|partial|unsupported",
      "confidence": 0.0,
      "issues": ["..."]
    }
  ],
  "stage_routing": {
    "transcription": {"selected_route_recommendation": "", "fallback_route": "", "reason": ""},
    "translation": {"selected_route_recommendation": "", "fallback_route": "", "reason": ""},
    "tts": {"selected_route_recommendation": "", "fallback_route": "", "reason": ""}
  },
  "blocking_issues": [{"code": "", "message": "", "component": "", "severity": "high"}],
  "warnings": [{"code": "", "message": "", "component": "", "severity": "medium"}],
  "next_actions": [{"order": 1, "action": "", "owner_hint": "app", "estimated_risk": "low"}],
  "ci_gate": {"verdict": "pass", "reason": ""}
}

Allowed enum values (do not include the `|` characters in JSON values):
- `blocking_issues[].severity`: `"high"`, `"medium"`, `"low"`
- `warnings[].severity`: `"high"`, `"medium"`, `"low"`
- `next_actions[].owner_hint`: `"app"`, `"infra"`, `"ops"`
- `next_actions[].estimated_risk`: `"low"`, `"medium"`, `"high"`
- `ci_gate.verdict`: `"pass"`, `"warn"`, `"fail"`

Rules:
- If evidence is missing, set values to `unknown` and lower confidence.
- Never assume acceleration success from provider presence alone.
- Flag silent fallback risk when route evidence is incomplete.
- Prefer conservative routing recommendations when confidence is low."

## Fast variants
- "Convert this `nvidia-smi` + app log output into the report JSON."
- "Generate a CI gate report JSON for Windows + DirectML + CPU fallback."
- "Produce a support-ticket JSON report for containerized CUDA device visibility failure."
