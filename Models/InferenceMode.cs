namespace Babel.Player.Models;

/// <summary>
/// Identifies the active inference execution path selected at startup
/// based on hardware availability and GPU-host health.
/// </summary>
public enum InferenceMode
{
    /// <summary>
    /// Default fallback: AI stages run as Python subprocesses on the local CPU.
    /// Used when neither a healthy containerized service nor a managed venv is available.
    /// </summary>
    SubprocessCpu,

    /// <summary>
    /// Docker-backed GPU inference host is healthy and responding.
    /// GPU (CUDA) availability within the host is reported separately via
    /// <see cref="Services.BootstrapDiagnostics.ContainerizedCudaAvailable"/>.
    /// This is the advanced GPU backend when Docker + NVIDIA Container Toolkit are present.
    /// </summary>
    Containerized,

    /// <summary>
    /// Managed Python venv GPU host is healthy and responding.
    /// This is the default low-friction local GPU path for phase 1.
    /// </summary>
    ManagedVenv,
}
