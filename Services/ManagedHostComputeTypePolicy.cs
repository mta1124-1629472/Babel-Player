using Babel.Player.Models;

namespace Babel.Player.Services;

public static class ManagedHostComputeTypePolicy
{
    /// <summary>
    /// Determines the compute type to request for the managed inference host based on hardware.
    /// Policy:
    ///   - Blackwell-capable GPU (SM 10.0+) with CUDA: request "float8" (FP8)
    ///   - Other GPU with CUDA: request "float16"
    ///   - CPU-only or no GPU: request "int8"
    /// 
    /// NOTE: This is the REQUESTED type. Python host will validate support and downgrade if necessary.
    /// </summary>
    public static string ResolveLaunchComputeType(HardwareSnapshot hardwareSnapshot, ComputeProfile profile)
    {
        if (profile == ComputeProfile.Gpu && hardwareSnapshot.HasCuda)
        {
            // Blackwell or newer: request FP8
            if (hardwareSnapshot.IsBlackwellCapable)
                return "float8";

            // Other CUDA GPU: use FP16
            return "float16";
        }

        // CPU-only or no GPU
        return "int8";
    }
}
