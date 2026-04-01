---
name: Operator Compatibility Audit
description: "Audit model graph operator compatibility across CUDA, DirectML, OpenVINO, NPU, and CPU paths to prevent hidden fallback and acceleration regressions."
argument-hint: "Provide model format/version, target providers/runtimes, hardware targets, precision mode, and observed symptoms."
agent: "Hardware Acceleration Architect"
---
Use this prompt when acceleration is enabled but performance is poor, unstable, or unexpectedly falling back.

## Paste this template
"Run an operator compatibility audit for our inference pipeline.

Model + runtime context:
- Model name/version: <...>
- Model format: <onnx/torchscript/tflite/...>
- Opset/version: <...>
- Precision mode: <fp32/fp16/int8/...>
- Runtime and provider candidates: <cuda/directml/openvino/npu/cpu>
- Execution shape/batch profile: <...>

Hardware targets:
- OS/version: <...>
- CPU: <...>
- GPU: <...>
- NPU: <... or none>
- Driver/runtime versions: <...>

Symptoms:
- What we expected: <...>
- What happened: <slow path/fallback/crash/numerical drift>
- Logs/errors/profiler notes: <...>

Required deliverables:
1) compatibility matrix by stage and provider (supported/partial/unsupported)
2) likely fallback points and why (op, precision, memory, kernel availability)
3) minimal remediation options ranked by risk
4) recommended default provider routing policy (CPU/GPU/NPU)
5) correctness validation plan (tolerance thresholds, sample set)
6) performance validation plan (baseline, warm/cold runs, pass/fail criteria)
7) deployment impact notes (native vs container, dependency changes)
8) monitoring additions to detect future hidden fallback"

## Specific checks to require
- Provider actually executes targeted kernels (not just provider selected)
- Unsupported ops are enumerated with exact replacement/partition strategy
- Precision-specific incompatibilities are called out (fp16/int8/npu constraints)
- Dynamic shape and memory pressure effects are evaluated
- Accuracy drift risk is explicitly assessed

## Fast variants
- "Find why ONNX Runtime CUDA selected provider still runs many ops on CPU."
- "Audit DirectML compatibility for this model and suggest graph-level changes."
- "Check whether OpenVINO + Intel NPU is viable for this stage or CPU should stay default."
- "Identify operators blocking fp16 adoption and provide low-risk mitigation options."
