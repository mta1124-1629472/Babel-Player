using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Transcription;

/// <summary>
/// Resolves CPU transcription compute type, thread count, and worker count from user settings and <see cref="HardwareSnapshot"/>.
/// </summary>
public static class CpuTranscriptionRuntimePolicy
{
    /// <summary>RAM reserved for OS and other apps when deriving worker budget (GB).</summary>
    public const double OsReserveGb = 4.0;

    /// <summary>Conservative RAM estimate per Whisper CPU worker (GB).</summary>
    public const double GbPerWorkerEstimate = 2.0;

    /// <summary>Physical cores to leave idle when deriving worker count from core budget.</summary>
    public const int ReservePhysicalCores = 1;

    public static int ComputeWorkersRamBudget(HardwareSnapshot hw)
    {
        if (hw.IsDetecting || hw.SystemRamGb <= 0)
            return 1;
        var avail = hw.SystemRamGb - OsReserveGb;
        if (avail <= 0)
            return 1;
        return Math.Max(1, (int)Math.Floor(avail / GbPerWorkerEstimate));
    }

    /// <summary>Physical-parallelism budget for workers (before RAM cap).</summary>
    public static int ComputeWorkersCoreBudget(HardwareSnapshot hw)
    {
        int physical;
        if (hw.CpuPhysicalCores is int p && p > 0)
        {
            physical = p;
        }
        else
        {
            var logical = hw.CpuCores > 0 ? hw.CpuCores : Environment.ProcessorCount;
            physical = Math.Max(1, logical / 2);
        }

        return Math.Max(1, physical - ReservePhysicalCores);
    }

    public static int ComputeAutoNumWorkers(HardwareSnapshot hw) =>
        Math.Max(1, Math.Min(ComputeWorkersCoreBudget(hw), ComputeWorkersRamBudget(hw)));

    /// <summary>Maximum manual workers: min(logical−1, RAM budget).</summary>
    public static int ComputeManualNumWorkersMax(HardwareSnapshot hw)
    {
        var logical = hw.CpuCores > 0 ? hw.CpuCores : Environment.ProcessorCount;
        var maxCpu = Math.Max(1, logical - 1);
        var maxRam = ComputeWorkersRamBudget(hw);
        return Math.Max(1, Math.Min(maxCpu, maxRam));
    }

    public static int ClampManualNumWorkers(int requested, HardwareSnapshot hw) =>
        Math.Max(1, Math.Min(requested, ComputeManualNumWorkersMax(hw)));

    public static int ResolveCpuThreads(int requested, HardwareSnapshot hw)
    {
        if (requested <= 0)
            return 0;
        var logical = hw.CpuCores > 0 ? hw.CpuCores : Environment.ProcessorCount;
        if (logical <= 0)
            logical = Environment.ProcessorCount;
        return Math.Max(1, Math.Min(requested, logical));
    }

    /// <summary>
    /// Resolves requested compute type for CPU inference. Downgrades AVX-512 dtypes when unsupported.
    /// </summary>
    public static string ResolveEffectiveComputeType(string requested, HardwareSnapshot hw, AppLog? log = null)
    {
        var r = string.IsNullOrWhiteSpace(requested) ? "int8" : requested.Trim().ToLowerInvariant();

        if (r == "auto")
            return hw.HasAvx512F ? "int8_float16" : "int8";

        if (r is "int8_float16" or "float16")
        {
            var hasAvx512 = !hw.IsDetecting && hw.HasAvx512F;
            if (!hasAvx512)
            {
                try
                {
                    hasAvx512 = Avx512F.IsSupported;
                }
                catch
                {
                    hasAvx512 = false;
                }
            }

            if (!hasAvx512)
            {
                log?.Warning(
                    $"CPU compute type '{r}' requires AVX-512F which is not available on this CPU. " +
                    "Downgrading to 'int8' for this run. Change in Settings > Transcription to suppress this warning.");
                return "int8";
            }
        }

        return r;
    }

    /// <summary>
    /// Builds effective transcription CPU parameters for local Faster Whisper / container CPU paths.
    /// </summary>
    public static CpuTranscriptionParameters ResolveForLocalCpu(
        AppSettings settings,
        HardwareSnapshot hw,
        AppLog? log = null)
    {
        var notes = new List<string>();
        var rawCompute = settings.TranscriptionCpuComputeType;
        var effectiveCompute = ResolveEffectiveComputeType(rawCompute, hw, log);
        if (!string.Equals(rawCompute, effectiveCompute, StringComparison.OrdinalIgnoreCase))
            notes.Add($"cpu_compute {rawCompute}→{effectiveCompute}");

        var threadsIn = settings.TranscriptionCpuThreads;
        var threadsOut = ResolveCpuThreads(threadsIn, hw);
        if (threadsIn > 0 && threadsOut != threadsIn)
            notes.Add($"cpu_threads {threadsIn}→{threadsOut}");

        int workers;
        if (settings.TranscriptionNumWorkersUseAuto)
            workers = ComputeAutoNumWorkers(hw);
        else
        {
            var rawW = settings.TranscriptionNumWorkers;
            workers = ClampManualNumWorkers(rawW, hw);
            if (rawW != workers)
                notes.Add($"cpu_workers {rawW}→{workers}");
        }

        return new CpuTranscriptionParameters(effectiveCompute, threadsOut, workers, notes);
    }

    /// <summary>
    /// Minimal snapshot for code paths that lack a full <see cref="HardwareSnapshot"/> (e.g. benchmarks).
    /// Uses intrinsics for AVX-512 detection.
    /// </summary>
    public static HardwareSnapshot CreateMinimalProbeSnapshot()
    {
        var avx512 = false;
        try
        {
            avx512 = Avx512F.IsSupported;
        }
        catch
        {
            avx512 = false;
        }

        return new HardwareSnapshot(
            IsDetecting: false,
            CpuName: null,
            CpuCores: Environment.ProcessorCount,
            HasAvx: true,
            HasAvx2: true,
            HasAvx512F: avx512,
            SystemRamGb: 0,
            GpuName: null,
            GpuVramMb: null,
            HasCuda: false,
            CudaVersion: null,
            HasOpenVino: false,
            OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: false,
            IsVsrDriverSufficient: false,
            NvidiaDriverVersion: null,
            GpuComputeCapability: null,
            CpuPhysicalCores: null);
    }

    public static TranscriptionRequest BuildTranscriptionRequest(
        AppSettings settings,
        HardwareSnapshot hw,
        string sourceAudioPath,
        string outputJsonPath,
        string modelName,
        string? languageHint,
        AppLog? log = null) =>
        BuildTranscriptionRequest(
            settings,
            hw,
            sourceAudioPath,
            outputJsonPath,
            modelName,
            languageHint,
            log,
            out _);

    public static TranscriptionRequest BuildTranscriptionRequest(
        AppSettings settings,
        HardwareSnapshot hw,
        string sourceAudioPath,
        string outputJsonPath,
        string modelName,
        string? languageHint,
        AppLog? log,
        out CpuTranscriptionParameters parameters)
    {
        parameters = ResolveForLocalCpu(settings, hw, log);
        return new TranscriptionRequest(
            sourceAudioPath,
            outputJsonPath,
            modelName,
            languageHint,
            parameters.CpuComputeType,
            parameters.CpuThreads,
            parameters.NumWorkers);
    }

    /// <summary>Single retry preset after a recoverable CPU transcription failure.</summary>
    public static TranscriptionRequest WithSafeCpuFallback(TranscriptionRequest request) =>
        request with
        {
            CpuComputeType = "int8",
            CpuThreads = 0,
            NumWorkers = 1,
        };

    /// <summary>Heuristic: errors that may succeed with fewer CPU resources.</summary>
    public static bool IsRecoverableCpuTranscriptionFailure(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;
        var e = errorMessage.ToLowerInvariant();
        return e.Contains("out of memory")
            || e.Contains("cannot allocate")
            || e.Contains("illegal instruction")
            || e.Contains("sigill")
            || e.Contains("killed")
            || e.Contains("worker");
    }
}

public sealed record CpuTranscriptionParameters(
    string CpuComputeType,
    int CpuThreads,
    int NumWorkers,
    IReadOnlyList<string> ResolutionNotes);
