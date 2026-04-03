using Babel.Player.Models;

namespace Babel.Player.Services;

public static class ManagedHostComputeTypePolicy
{
    /// <summary>
    /// Determines the compute type to request for the managed inference host based on hardware.
    /// Reliability-first policy:
    ///   - Any managed GPU host uses "float16" so shared translation/TTS stages stay truthful and ready.
    ///   - CPU-only or no GPU uses "int8".
    ///
    /// Float8 remains a follow-up once all managed GPU stages support it end-to-end.
    /// </summary>
    public static string ResolveLaunchComputeType(HardwareSnapshot hardwareSnapshot, ComputeProfile profile)
    {
        if (profile == ComputeProfile.Gpu && hardwareSnapshot.HasCuda)
            return "float16";

        // CPU-only or no GPU
        return "int8";
    }
}
