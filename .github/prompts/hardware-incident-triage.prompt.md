---
name: Hardware Incident Triage
description: "Triage failed or degraded hardware acceleration in Babel Player with evidence-first diagnosis, safe mitigation, and validated recovery plan."
argument-hint: "Provide OS, hardware, runtime and provider details, symptoms, recent changes, and logs/metrics if available."
agent: "Hardware Acceleration Architect"
---
Use this prompt when acceleration fails, underperforms, or behaves inconsistently.

## Paste this template
"Run a hardware acceleration incident triage for Babel Player.

Environment:
- OS/version: <...>
- CPU: <...>
- GPU: <...>
- NPU: <... or none>
- .NET/runtime: <...>
- Inference runtime and provider: <onnx/directml/cuda/openvino/python subprocess/...>
- Deployment mode: <native/docker/service>

Incident:
- Stage affected: <transcription/translation/tts/multiple>
- Symptom: <fallback to CPU, crash, slow, OOM, device not found, inconsistent output>
- Start time / change window: <...>
- Recent changes (driver/runtime/code/config): <...>

Evidence:
- Relevant logs/errors: <...>
- Benchmark deltas: <...>
- Utilization signals (GPU/NPU/CPU/RAM): <...>

Deliverables required:
1) probable root causes (ranked)
2) immediate safe mitigation
3) permanent fix plan
4) verification checklist + exact commands
5) monitoring/alerting additions
6) risk + rollback plan"

## Fast prompts
- "GPU is detected but inference still runs on CPU. Give me a ranked root-cause list and exact checks."
- "After driver update, TTS latency doubled. Give mitigation now and a durable fix plan."
- "Containerized runtime cannot see GPU/NPU devices. Provide pass-through checks and fallback strategy."
- "DirectML path crashes on specific model. Provide compatibility diagnosis and safe fallback path."
- "NPU path is slower than CPU for small batches. Recommend routing policy by batch size and SLA."
