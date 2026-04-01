namespace Babel.Player.Models;

/// <summary>
/// Identifies the active inference execution path selected at startup
/// based on hardware availability and container health.
/// </summary>
public enum InferenceMode
{
    /// <summary>
    /// Default fallback: AI stages run as Python subprocesses on the local CPU.
    /// Used when neither a healthy containerized service nor a managed venv is available.
    /// </summary>
    SubprocessCpu,

    /// <summary>
    /// Containerized inference service is healthy and responding.
    /// All AI pipeline stages route to the Docker inference service.
    /// GPU (CUDA) availability within the container is reported separately via
    /// <see cref="Services.BootstrapDiagnostics.ContainerizedCudaAvailable"/>.
    /// This is the preferred mode when Docker + NVIDIA Container Toolkit are present.
    /// </summary>
    Containerized,

    /// <summary>
    /// PLACEHOLDER: Managed Python venv with optional GPU acceleration.
    /// Intended for machines where Docker is not available but a local GPU path can be
    /// bootstrapped automatically. Not yet implemented — explicit placeholder per AGENTS.md.
    /// </summary>
    ManagedVenv,
}
