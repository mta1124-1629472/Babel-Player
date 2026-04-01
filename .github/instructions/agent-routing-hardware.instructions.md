---
name: Hardware & Performance Agent Routing
description: Use when tasks involve AI inference acceleration, runtime and provider selection, benchmark execution, performance regressions, or Avalonia runtime performance tuning in Babel Player.
---
# Agent Routing Guide (Babel Player)

Use the following routing defaults for acceleration/performance tasks:

1. **Hardware Acceleration Architect**
   - Use for CUDA/DirectML/OpenVINO/NPU/CPU-SIMD integration and fallback design.
   - Use for containerized inference trade-offs and rollout plans.

2. **Babel Player Benchmark Runner**
   - Use for standardized stage benchmarks and baseline matrix generation.

3. **.NET Avalonia Performance Sweep**
   - Use for UI-thread, rendering, allocation, and memory optimization sweeps.

4. **Perf Regression Review**
   - Use when baseline vs candidate benchmark artifacts need regression analysis.

## Skills and MCP usage
- Prefer invoking relevant skills when workflow structure helps quality (planning, code-review, proactivity).
- For Avalonia-specific framework behavior/API clarification, use the Avalonia docs MCP.
- For general package/framework docs and code examples, use Context7 documentation tools.
- For Microsoft/.NET/DirectML implementation guidance, prefer official Microsoft docs/samples.
- For incident response tasks, prioritize evidence collection and diagnostics before proposing a solution.

## Incident routing sequence
1. Route to **Hardware Acceleration Architect** for initial triage and mitigation plan.
2. If performance claims are involved, route to **Babel Player Benchmark Runner** for controlled measurement.
3. If candidate changes regress or differ from baseline, route to **Perf Regression Review**.
4. If UI/desktop responsiveness is affected, route to **.NET Avalonia Performance Sweep**.

## Plugin/extension recommendations
- It is acceptable to recommend lightweight tooling/plugins that improve hardware diagnostics quality.
- Recommendations must include: purpose, expected signal, and fallback path if the plugin is unavailable.

## Routing notes
- Start with **Hardware Acceleration Architect** when uncertainty exists about where acceleration should happen (CPU vs GPU vs NPU).
- Escalate to benchmark/perf agents when quantitative validation is required.
- Keep recommendations production-safe: incremental rollout, observability, and explicit CPU fallback.

## Discovery-first recommendation rule
- Before recommending an inference path, capture hardware and driver/runtime readiness (CPU features, GPU/NPU capability, provider availability, and compatibility constraints).
- Base recommendations on stage-specific routing, not a single global provider choice.
- If readiness evidence is incomplete, return assumptions explicitly and choose conservative defaults.

## Related prompt templates
- `hardware-discovery-routing-strategy.prompt.md` for startup probes and runtime route policy.
- `provider-capability-matrix-generator.prompt.md` for environment/provider capability artifacts.
- `driver-runtime-compatibility-report.prompt.md` for machine-readable diagnostics artifacts.
- `hardware-incident-triage.prompt.md` for ranked root-cause and mitigation workflows.

## Incident/support workflow (JSON-first)
Use this sequence for support tickets, CI failures, or production incidents:

1. Generate a normalized diagnostics artifact with `driver-runtime-compatibility-report.prompt.md`.
2. Use `hardware-incident-triage.prompt.md` to produce ranked root causes and immediate mitigation.
3. If fallback or poor acceleration is suspected, run `operator-compatibility-audit.prompt.md`.
4. If route policy is unclear, use `hardware-discovery-routing-strategy.prompt.md`.
5. If release planning is needed, produce `provider-capability-matrix-generator.prompt.md`.

Rules:
- Prefer JSON artifact generation before narrative triage whenever evidence is available.
- Do not claim acceleration readiness without compatibility + correctness + performance signals.
- Always include explicit fallback behavior and logging expectations in final recommendations.
