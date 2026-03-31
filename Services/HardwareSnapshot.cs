using System;
using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;

namespace Babel.Player.Services;

/// <summary>
/// Captures a point-in-time snapshot of the machine's hardware capabilities.
/// Call <see cref="Run"/> once at startup (off the UI thread) and store the result.
/// All detection probes are best-effort — failures produce null/false, never exceptions.
/// </summary>
public sealed record HardwareSnapshot(
    bool IsDetecting,
    string? CpuName,
    int CpuCores,
    bool HasAvx2,
    bool HasAvx512,
    double SystemRamGb,
    string? GpuName,
    long? GpuVramMb,
    bool HasCuda,
    string? CudaVersion,
    bool HasOpenVino,
    string? OpenVinoVersion,
    string? NpuLabel)
{
    /// <summary>Placeholder shown while background detection is still running.</summary>
    public static HardwareSnapshot Detecting { get; } = new(
        IsDetecting: true,
        CpuName: null, CpuCores: 0,
        HasAvx2: false, HasAvx512: false,
        SystemRamGb: 0,
        GpuName: null, GpuVramMb: null,
        HasCuda: false, CudaVersion: null,
        HasOpenVino: false, OpenVinoVersion: null,
        NpuLabel: null);

    // ── Formatted display lines ────────────────────────────────────────────────

    public string CpuLine
    {
        get
        {
            if (IsDetecting) return "Detecting…";
            var name = CpuName ?? "Unknown CPU";
            var cores = CpuCores > 0 ? $" · {CpuCores}c" : "";
            var avx2 = HasAvx2 ? " · AVX2 ✓" : " · AVX2 ✗";
            var avx512 = HasAvx512 ? " · AVX-512 ✓" : " · AVX-512 ✗";
            return $"{name}{cores}{avx2}{avx512}";
        }
    }

    public string GpuLine
    {
        get
        {
            if (IsDetecting) return "Detecting…";
            if (GpuName == null) return "—";
            var vram = GpuVramMb.HasValue ? $" · {GpuVramMb.Value / 1024.0:F0} GB VRAM" : "";
            var cuda = HasCuda ? $" · CUDA {CudaVersion ?? "✓"}" : "";
            return $"{GpuName}{vram}{cuda}";
        }
    }

    public string RamLine
    {
        get
        {
            if (IsDetecting) return "Detecting…";
            return SystemRamGb > 0 ? $"{SystemRamGb:F0} GB" : "—";
        }
    }

    public string NpuLine => IsDetecting ? "Detecting…" : (NpuLabel ?? "—");

    public string LibsLine
    {
        get
        {
            if (IsDetecting) return "Detecting…";
            var ov = HasOpenVino
                ? $"OpenVINO {OpenVinoVersion ?? "✓"}"
                : "OpenVINO ✗";
            return ov;
        }
    }

    // ── Detection ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Probes the machine and returns a fully populated snapshot.
    /// Blocking — call from a background thread.
    /// </summary>
    public static HardwareSnapshot Run()
    {
        var cpuName   = DetectCpuName();
        var cpuCores  = Environment.ProcessorCount;
        var hasAvx2   = TryAvx2();
        var hasAvx512 = TryAvx512();
        var ramGb     = DetectRamGb();

        var (gpuName, gpuVramMb) = DetectGpu();
        var (hasCuda, cudaVer)   = DetectCuda();
        var (hasOv, ovVer)       = DetectOpenVino();
        var npuLabel             = InferNpu(cpuName);

        return new HardwareSnapshot(
            IsDetecting: false,
            CpuName: cpuName, CpuCores: cpuCores,
            HasAvx2: hasAvx2, HasAvx512: hasAvx512,
            SystemRamGb: ramGb,
            GpuName: gpuName, GpuVramMb: gpuVramMb,
            HasCuda: hasCuda, CudaVersion: cudaVer,
            HasOpenVino: hasOv, OpenVinoVersion: ovVer,
            NpuLabel: npuLabel);
    }

    // ── CPU ────────────────────────────────────────────────────────────────────

    private static string? DetectCpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var val = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", null) as string;
                return val?.Trim();
            }
            catch { /* fall through */ }
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var lines = System.IO.File.ReadAllLines("/proc/cpuinfo");
                foreach (var line in lines)
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = line.IndexOf(':');
                        if (idx >= 0) return line[(idx + 1)..].Trim();
                    }
                }
            }
            catch { /* fall through */ }
        }

        return null;
    }

    private static bool TryAvx2()
    {
        try { return Avx2.IsSupported; }
        catch { return false; }
    }

    private static bool TryAvx512()
    {
        try { return Avx512F.IsSupported; }
        catch { return false; }
    }

    // ── RAM ────────────────────────────────────────────────────────────────────

    private static double DetectRamGb()
    {
        try
        {
            // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes reflects physical RAM
            // on a non-containerised machine, which is the typical desktop scenario.
            var info = GC.GetGCMemoryInfo();
            return info.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        }
        catch { return 0; }
    }

    // ── GPU (via nvidia-smi) ───────────────────────────────────────────────────

    private static (string? name, long? vramMb) DetectGpu()
    {
        var output = RunAndCapture("nvidia-smi",
            "--query-gpu=name,memory.total --format=csv,noheader,nounits");
        if (output == null) return (null, null);

        var line = output.Trim().Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(line)) return (null, null);

        var parts = line.Split(',');
        var name  = parts.Length > 0 ? parts[0].Trim() : null;
        long? vram = null;
        if (parts.Length > 1 && long.TryParse(parts[1].Trim(), out var mb))
            vram = mb;

        return (name, vram);
    }

    // ── CUDA (via nvidia-smi header) ──────────────────────────────────────────

    private static (bool hasCuda, string? version) DetectCuda()
    {
        var output = RunAndCapture("nvidia-smi", "");
        if (output == null) return (false, null);

        var m = Regex.Match(output, @"CUDA Version:\s*(\S+)");
        return m.Success ? (true, m.Groups[1].Value) : (false, null);
    }

    // ── OpenVINO (via Python import probe) ────────────────────────────────────

    private static (bool hasOv, string? version) DetectOpenVino()
    {
        var python = DependencyLocator.FindPython();
        if (python == null) return (false, null);

        var output = RunAndCapture(python,
            "-c \"import openvino; print(openvino.__version__)\"");
        if (output == null) return (false, null);

        var ver = output.Trim();
        return (!string.IsNullOrEmpty(ver), string.IsNullOrEmpty(ver) ? null : ver);
    }

    // ── NPU heuristic ──────────────────────────────────────────────────────────

    private static string? InferNpu(string? cpuName)
    {
        if (cpuName == null) return null;
        if (cpuName.Contains("Core Ultra", StringComparison.OrdinalIgnoreCase))
            return "Intel NPU";
        if (cpuName.Contains("Snapdragon X", StringComparison.OrdinalIgnoreCase))
            return "Qualcomm NPU";
        return null;
    }

    // ── Process helper ─────────────────────────────────────────────────────────

    private static string? RunAndCapture(string exe, string args, int timeoutMs = 3000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return null;
            }
            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
