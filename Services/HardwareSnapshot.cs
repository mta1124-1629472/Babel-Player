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
    string? NpuLabel,
    bool IsRtxCapable,
    bool IsVsrDriverSufficient,
    string? NvidiaDriverVersion,
    bool IsHdrDisplayAvailable)
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
        NpuLabel: null,
        IsRtxCapable: false,
        IsVsrDriverSufficient: false,
        NvidiaDriverVersion: null,
        IsHdrDisplayAvailable: false);

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

        var (gpuName, gpuVramMb)          = DetectGpu();
        var (hasCuda, cudaVer)            = DetectCuda();
        var (hasOv, ovVer)                = DetectOpenVino();
        var npuLabel                      = InferNpu(cpuName);
        var (driverVer, isVsrSufficient)  = DetectNvidiaDriver();
        var isRtx                         = IsRtxGpu(gpuName);
        var isHdr                         = DetectHdrDisplay();

        return new HardwareSnapshot(
            IsDetecting: false,
            CpuName: cpuName, CpuCores: cpuCores,
            HasAvx2: hasAvx2, HasAvx512: hasAvx512,
            SystemRamGb: ramGb,
            GpuName: gpuName, GpuVramMb: gpuVramMb,
            HasCuda: hasCuda, CudaVersion: cudaVer,
            HasOpenVino: hasOv, OpenVinoVersion: ovVer,
            NpuLabel: npuLabel,
            IsRtxCapable: isRtx,
            IsVsrDriverSufficient: isVsrSufficient,
            NvidiaDriverVersion: driverVer,
            IsHdrDisplayAvailable: isHdr);
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

    // ── RTX capable ───────────────────────────────────────────────────────────

    private static bool IsRtxGpu(string? gpuName)
    {
        if (gpuName == null) return false;
        return gpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase);
    }

    // ── NVIDIA driver version (via nvidia-smi) ────────────────────────────────

    /// <summary>
    /// Returns the driver version string and whether it meets the minimum required for
    /// RTX Video Super Resolution (≥ 551.23).
    /// </summary>
    private static (string? version, bool isSufficient) DetectNvidiaDriver()
    {
        var output = RunAndCapture("nvidia-smi",
            "--query-gpu=driver_version --format=csv,noheader,nounits");
        if (output == null) return (null, false);

        var ver = output.Trim().Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(ver)) return (null, false);

        // Driver versions on Windows follow a nnn.nn format (e.g. "551.23", "572.16")
        bool sufficient = false;
        var parts = ver.Split('.');
        if (parts.Length >= 2
            && int.TryParse(parts[0], out int major)
            && int.TryParse(parts[1], out int minor))
        {
            sufficient = major > 551 || (major == 551 && minor >= 23);
        }

        return (ver, sufficient);
    }

    // ── CUDA (via nvidia-smi header) ──────────────────────────────────────────

    private static (bool hasCuda, string? version) DetectCuda()
    {
        var output = RunAndCapture("nvidia-smi", "");
        if (output == null) return (false, null);

        var m = Regex.Match(output, @"CUDA Version:\s*(\S+)");
        return m.Success ? (true, m.Groups[1].Value) : (false, null);
    }

    // ── HDR display (Windows-only, WMI fallback) ──────────────────────────────

    /// <summary>
    /// Returns true when at least one connected display reports HDR capability via the
    /// Windows registry (HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\SceneChangeDetect).
    /// Falls back gracefully to false on non-Windows or when the key is absent.
    /// </summary>
    private static bool DetectHdrDisplay()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            // Windows stores per-display HDR support under the display adapter key.
            // The presence of any HDR-enabled monitor is surfaced via WMI or the registry.
            // We use a lightweight registry probe that works without WMI activation.
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration");
            if (key == null) return false;

            foreach (var subkeyName in key.GetSubKeyNames())
            {
                using var subkey = key.OpenSubKey(subkeyName);
                if (subkey == null) continue;
                foreach (var childName in subkey.GetSubKeyNames())
                {
                    using var child = subkey.OpenSubKey(childName);
                    if (child == null) continue;
                    // "HDRSupported" value present and non-zero → HDR display
                    var hdrVal = child.GetValue("HDRSupported");
                    if (hdrVal is int i && i != 0) return true;
                    if (hdrVal is uint u && u != 0) return true;
                }
            }
        }
        catch { /* fall through */ }

        return false;
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
