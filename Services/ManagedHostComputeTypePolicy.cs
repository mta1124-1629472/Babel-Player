using Babel.Player.Models;

namespace Babel.Player.Services;

public static class ManagedHostComputeTypePolicy
{
    /// <summary>
    /// Determines the compute type to request for the managed inference host based on hardware.
    /// Fallback policy:
    ///   - Blackwell CUDA GPUs request "float8" by default.
    ///   - Older CUDA-capable NVIDIA GPUs request "float16".
    ///   - CPU-only or non-NVIDIA paths use "int8".
    ///
    /// Stage-specific runtime validation can still downgrade float8 where unsupported.
    /// </summary>
    public static string ResolveLaunchComputeType(HardwareSnapshot hardwareSnapshot, ComputeProfile profile)
    {
        if (profile == ComputeProfile.Gpu && hardwareSnapshot.HasCuda)
            return hardwareSnapshot.IsBlackwellCapable ? "float8" : "float16";

        // CPU-only or no GPU
        return "int8";
    }
}
