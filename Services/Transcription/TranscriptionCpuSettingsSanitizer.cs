using System;
using System.Collections.Generic;
using System.Linq;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Transcription;

/// <summary>
/// Validates Advanced CPU transcription settings against <see cref="HardwareSnapshot"/> and mutates values in place.
/// </summary>
public static class TranscriptionCpuSettingsSanitizer
{
    public static readonly string[] AllComputeTypes =
        ["auto", "int8", "int8_float16", "float32", "float16"];

    /// <summary>
    /// Compute types shown in UI for the current hardware (AVX-512-only options omitted when unsupported).
    /// </summary>
    public static string[] GetSelectableComputeTypes(HardwareSnapshot hw)
    {
        if (hw.IsDetecting)
            return AllComputeTypes.Where(t => t is not ("int8_float16" or "float16")).ToArray();
        if (hw.HasAvx512F)
            return AllComputeTypes;
        return AllComputeTypes.Where(t => t is not ("int8_float16" or "float16")).ToArray();
    }

    /// <summary>
    /// Sanitizes settings; returns human-readable messages for any correction (for toast / log).
    /// </summary>
    public static IReadOnlyList<string> Sanitize(AppSettings settings, HardwareSnapshot hw)
    {
        var messages = new List<string>();
        var lower = settings.TranscriptionCpuComputeType?.Trim().ToLowerInvariant() ?? "auto";
        if (!AllComputeTypes.Contains(lower))
        {
            settings.TranscriptionCpuComputeType = "auto";
            messages.Add($"CPU compute type was invalid; reset to auto.");
        }
        else
        {
            settings.TranscriptionCpuComputeType = lower;
        }

        if (!hw.IsDetecting && !hw.HasAvx512F &&
            settings.TranscriptionCpuComputeType is "int8_float16" or "float16")
        {
            settings.TranscriptionCpuComputeType = "int8";
            messages.Add("CPU compute type requires AVX-512F; switched to int8.");
        }

        settings.TranscriptionCpuThreads = Math.Max(0, settings.TranscriptionCpuThreads);
        var logical = hw.CpuCores > 0 ? hw.CpuCores : Environment.ProcessorCount;
        if (settings.TranscriptionCpuThreads > 0 && logical > 0 &&
            settings.TranscriptionCpuThreads > logical)
        {
            var prev = settings.TranscriptionCpuThreads;
            settings.TranscriptionCpuThreads = logical;
            messages.Add($"CPU threads capped from {prev} to {logical} (logical processors).");
        }

        if (settings.TranscriptionNumWorkersUseAuto)
        {
            // Display field may hold stale number; effective value comes from policy at runtime.
        }
        else
        {
            var prev = settings.TranscriptionNumWorkers;
            var clamped = CpuTranscriptionRuntimePolicy.ClampManualNumWorkers(prev, hw);
            if (clamped != prev)
            {
                settings.TranscriptionNumWorkers = clamped;
                messages.Add($"CPU workers capped from {prev} to {clamped}.");
            }
        }

        return messages;
    }
}
