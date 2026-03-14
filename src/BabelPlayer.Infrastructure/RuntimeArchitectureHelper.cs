using System.Runtime.InteropServices;

namespace BabelPlayer.Infrastructure;

public static class RuntimeArchitectureHelper
{
    public static Architecture GetCurrentArchitecture() => RuntimeInformation.ProcessArchitecture;

    public static string ToRuntimeIdentifier(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new NotSupportedException($"Unsupported runtime architecture '{architecture}'.")
        };
    }

    public static string ToFolderName(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new NotSupportedException($"Unsupported runtime architecture '{architecture}'.")
        };
    }
}
