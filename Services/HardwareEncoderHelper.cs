using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Resolves the best available ffmpeg video encoder for the export stage.
/// Called at export time — detection is deferred until the export feature is implemented.
/// </summary>
public static class HardwareEncoderHelper
{
    /// <summary>
    /// Returns the ffmpeg encoder name to use for video export.
    /// Respects the user's explicit <see cref="AppSettings.VideoExportEncoder"/> value;
    /// falls back to hardware detection via <see cref="HardwareSnapshot"/>, then software.
    /// </summary>
    public static string ResolveEncoder(AppSettings settings, HardwareSnapshot hw)
    {
        if (settings.VideoExportEncoder != "auto")
            return settings.VideoExportEncoder;

        // NVIDIA — presence of CUDA implies NVENC is available
        if (hw.HasCuda)
            return "h264_nvenc";

        // AMD / Intel — inferred from GPU display name.
        // HardwareSnapshot currently only runs nvidia-smi; AMD and Intel names come from
        // the GpuName property when nvidia-smi is absent or returns no GPU.
        var gpuName = hw.GpuName?.ToLowerInvariant() ?? "";
        if (gpuName.Contains("amd") || gpuName.Contains("radeon"))
            return "h264_amf";
        if (gpuName.Contains("intel") || gpuName.Contains("arc"))
            return "h264_qsv";

        // Software fallback — always available wherever ffmpeg is installed
        return "libx264";
    }
}
