using System.Collections.Generic;
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

    /// <summary>
    /// Appends encoder-specific quality / rate-control flags after <c>-c:v &lt;encoder&gt;</c>.
    /// Also appends <c>-pix_fmt yuv420p</c> for broad player compatibility.
    /// </summary>
    public static void AppendRecommendedVideoQualityArgs(IList<string> args, string encoder)
    {
        switch (encoder)
        {
            case "libx264":
                args.Add("-crf");
                args.Add("20");
                args.Add("-preset");
                args.Add("medium");
                break;
            case "libx265":
                args.Add("-crf");
                args.Add("22");
                args.Add("-preset");
                args.Add("medium");
                break;
            case "h264_nvenc":
                args.Add("-preset");
                args.Add("p4");
                args.Add("-rc");
                args.Add("vbr");
                args.Add("-cq");
                args.Add("23");
                break;
            case "hevc_nvenc":
                args.Add("-preset");
                args.Add("p4");
                args.Add("-rc");
                args.Add("vbr");
                args.Add("-cq");
                args.Add("26");
                break;
            case "h264_amf":
            case "hevc_amf":
                args.Add("-quality");
                args.Add("balanced");
                break;
            case "h264_qsv":
            case "hevc_qsv":
                args.Add("-preset");
                args.Add("medium");
                args.Add("-global_quality");
                args.Add("25");
                break;
            default:
                // Unknown / future encoder — rely on ffmpeg defaults for that codec.
                break;
        }

        args.Add("-pix_fmt");
        args.Add("yuv420p");
    }
}
