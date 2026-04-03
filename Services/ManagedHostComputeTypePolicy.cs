using Babel.Player.Models;

namespace Babel.Player.Services;

public static class ManagedHostComputeTypePolicy
{
    public static string ResolveLaunchComputeType(HardwareSnapshot hardwareSnapshot, ComputeProfile profile)
    {
        if (profile == ComputeProfile.Gpu && hardwareSnapshot.HasCuda)
            return "float16";

        return "int8";
    }
}
