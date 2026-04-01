---
name: Hardware Profile Benchmark Planning
description: Use when creating Babel Player benchmark plans, baseline matrices, or stage benchmark runs; auto-inject machine capabilities and runtime environment into the plan before execution.
---
# Hardware Profile Injection Rules

When preparing any benchmark run plan for Babel Player, prepend an Environment Snapshot block before benchmark steps.

## Required Environment Snapshot Fields
- MachineId label (human readable)
- OS name and version
- CPU model, physical cores, logical threads
- RAM total and available
- GPU model and VRAM (or `none`)
- Disk type/path used for benchmark artifacts
- .NET SDK/runtime version
- Python version
- Provider runtime versions if available

## Injection Contract
1. Collect snapshot values first.
2. Add snapshot to the benchmark plan under section title: `Environment Snapshot`.
3. Add hardware profile token to every matrix variant:
- Format: `<precision>_<cores>c<threads>t_<ram_gb>g`
- Example: `fp16_8c16t_32g`
4. If any required field is unknown, keep the field and set value to `unknown`; do not omit.

## Comparison Safety
- Do not compare benchmark results across different hardware profiles as a single performance conclusion.
- If hardware profile differs, label comparison as `cross-hardware` and downgrade confidence.

## Reporting Requirement
Every benchmark report must include a short note:
- `Hardware Bottleneck Risk: low|medium|high`
- One-line reason tied to observed utilization or constraints.
