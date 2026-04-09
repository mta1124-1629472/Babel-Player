using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    bool HasAvx,
    bool HasAvx2,
    bool HasAvx512F,
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
    string? GpuComputeCapability = null)
{
    /// <summary>Placeholder shown while background detection is still running.</summary>
    public static HardwareSnapshot Detecting { get; } = new(
        IsDetecting: true,
        CpuName: null, CpuCores: 0,
        HasAvx: false, HasAvx2: false, HasAvx512F: false,
        SystemRamGb: 0,
        GpuName: null, GpuVramMb: null,
        HasCuda: false, CudaVersion: null,
        HasOpenVino: false, OpenVinoVersion: null,
        NpuLabel: null,
        IsRtxCapable: false,
        IsVsrDriverSufficient: false,
        NvidiaDriverVersion: null,
        GpuComputeCapability: null);

    // ── Formatted display lines ────────────────────────────────────────────────

    public string CpuLine
    {
        get
        {
            if (IsDetecting) return "Detecting\u2026";
            var name  = CpuName ?? "Unknown CPU";
            var cores = CpuCores > 0 ? $" \u00b7 {CpuCores}c" : "";
            return $"{name}{cores} \u00b7 {CpuVectorLine}";
        }
    }

    /// <summary>
    /// Human-readable CPU vector capability tier.
    /// Suitable for both the hardware panel and bootstrap diagnostics.
    /// Examples: "AVX-512F", "AVX2", "AVX (no AVX2 \u2014 reduced performance)", "none detected"
    /// </summary>
    public string CpuVectorLine
    {
        get
        {
            if (IsDetecting) return "Detecting\u2026";
            if (HasAvx512F) return "AVX-512F";
            if (HasAvx2)    return "AVX2";
            if (HasAvx)     return "AVX (no AVX2 \u2014 reduced inference performance)";
            return "none detected (inference will be significantly slower)";
        }
    }

    public string GpuLine
    {
        get
        {
            if (IsDetecting) return "Detecting\u2026";
            if (GpuName == null) return "\u2014";
            var vram = GpuVramMb.HasValue ? $" \u00b7 {GpuVramMb.Value / 1024.0:F0} GB VRAM" : "";
            var cuda = HasCuda ? $" \u00b7 CUDA {CudaVersion ?? "\u2713"}" : "";
            return $"{GpuName}{vram}{cuda}";
        }
    }

    public string RamLine
    {
        get
        {
            if (IsDetecting) return "Detecting\u2026";
            return SystemRamGb > 0 ? $"{SystemRamGb:F0} GB" : "\u2014";
        }
    }

    public string NpuLine => IsDetecting ? "Detecting\u2026" : (NpuLabel ?? "\u2014");

    public string LibsLine
    {
        get
        {
            if (IsDetecting) return "Detecting\u2026";
            var ov = HasOpenVino
                ? $"OpenVINO {OpenVinoVersion ?? "\u2713"}"
                : "OpenVINO \u2717";
            return ov;
        }
    }

    /// <summary>
    /// True if GPU has Blackwell or newer compute capability (SM 10.0+).
    /// Used to gate FP8 compute type selection.
    /// </summary>
    public bool IsBlackwellCapable
    {
        get
        {
            if (GpuComputeCapability == null) return false;
            var parts = GpuComputeCapability.Split('.');
            if (parts.Length < 1 || !int.TryParse(parts[0], out var major))
                return false;
            return major >= 10;
        }
    }

    // ── Detection ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Probes the machine and returns a fully populated snapshot.
    /// Blocking — call from a background thread.
    /// </summary>
    /// <param name="findPython">
    /// Optional delegate that returns the path to a Python executable.
    /// Used for GPU compute capability and OpenVINO detection.
    /// Pass <c>null</c> to skip those two probes (useful when running outside Babel Player).
    /// Defaults to <c>DependencyLocator.FindPython</c> when not specified.
    /// </param>
    public static HardwareSnapshot Run(Func<string?>? findPython = null)
    {
        findPython ??= DependencyLocator.FindPython;

        var cpuName   = DetectCpuName();
        var cpuCores  = Environment.ProcessorCount;
        var hasAvx    = TryAvx();
        var hasAvx2   = TryAvx2();
        var hasAvx512 = TryAvx512F();
        var ramGb     = DetectRamGb();

        var (gpuName, gpuVramMb)          = DetectGpu();
        var (hasCuda, cudaVer)            = DetectCuda();
        var (hasOv, ovVer)                = DetectOpenVino(findPython);
        var npuLabel                      = InferNpu(cpuName);
        var (driverVer, isVsrSufficient)  = DetectNvidiaDriver();
        var isRtx                         = IsRtxGpu(gpuName);
        var gpuComputeCapability          = DetectGpuComputeCapability(hasCuda, findPython);

        return new HardwareSnapshot(
            IsDetecting: false,
            CpuName: cpuName, CpuCores: cpuCores,
            HasAvx: hasAvx, HasAvx2: hasAvx2, HasAvx512F: hasAvx512,
            SystemRamGb: ramGb,
            GpuName: gpuName, GpuVramMb: gpuVramMb,
            HasCuda: hasCuda, CudaVersion: cudaVer,
            HasOpenVino: hasOv, OpenVinoVersion: ovVer,
            NpuLabel: npuLabel,
            IsRtxCapable: isRtx,
            IsVsrDriverSufficient: isVsrSufficient,
            NvidiaDriverVersion: driverVer,
            GpuComputeCapability: gpuComputeCapability);
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

    private static bool TryAvx()
    {
        try { return Avx.IsSupported; }
        catch { return false; }
    }

    private static bool TryAvx2()
    {
        try { return Avx2.IsSupported; }
        catch { return false; }
    }

    private static bool TryAvx512F()
    {
        try { return Avx512F.IsSupported; }
        catch { return false; }
    }

    // ── RAM ────────────────────────────────────────────────────────────────────

    private static double DetectRamGb()
    {
        try
        {
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

    // ── GPU compute capability (via Python torch) ─────────────────────────────

    private static string? DetectGpuComputeCapability(bool hasCuda, Func<string?> findPython)
    {
        if (!hasCuda) return null; // No CUDA available

        var python = findPython();
        if (python == null) return null;

        // Use Python to probe torch.cuda.get_device_capability(0)
        var pythonScript = "import torch; cap = torch.cuda.get_device_capability(0); print(f\"{cap[0]}.{cap[1]}\")";
        var output = RunAndCapture(python, $"-c \"{pythonScript}\"");
        if (output == null) return null;

        var trimmed = output.Trim();
        // Validate format: should be "major.minor"
        var parts = trimmed.Split('.');
        if (parts.Length == 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
            return trimmed;

        return null;
    }

    // ── NVIDIA driver version (via nvidia-smi) ────────────────────────────────

    private static (string? version, bool isSufficient) DetectNvidiaDriver()
    {
        var output = RunAndCapture("nvidia-smi",
            "--query-gpu=driver_version --format=csv,noheader,nounits");
        if (output == null) return (null, false);

        var ver = output.Trim().Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(ver)) return (null, false);

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

    // ── Active HDR display (Windows-only) ─────────────────────────────────────

    public static bool QueryActiveHdrDisplay()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var factoryIid = IID_IDXGIFactory1;
            var hr = CreateDXGIFactory1(in factoryIid, out var factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero)
            {
                Debug.WriteLine($"HDR detection: CreateDXGIFactory1 failed with HRESULT 0x{hr:X8}.");
                return false;
            }

            try
            {
                var enumAdapters1 = GetDxgiMethod<EnumAdapters1Delegate>(factoryPtr, slot: VtblSlot_IDXGIFactory1_EnumAdapters1);

                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    hr = enumAdapters1(factoryPtr, adapterIndex, out var adapterPtr);
                    if (hr == DXGI_ERROR_NOT_FOUND)
                        break;

                    if (hr < 0 || adapterPtr == IntPtr.Zero)
                        continue;

                    try
                    {
                        var enumOutputs = GetDxgiMethod<EnumOutputsDelegate>(adapterPtr, slot: VtblSlot_IDXGIAdapter_EnumOutputs);

                        for (uint outputIndex = 0; ; outputIndex++)
                        {
                            hr = enumOutputs(adapterPtr, outputIndex, out var outputPtr);
                            if (hr == DXGI_ERROR_NOT_FOUND)
                                break;

                            if (hr < 0 || outputPtr == IntPtr.Zero)
                                continue;

                            try
                            {
                                if (TryGetOutputDesc1(outputPtr, out var outputDesc)
                                    && outputDesc.AttachedToDesktop
                                    && IsHdrColorSpace(outputDesc.ColorSpace))
                                {
                                    return true;
                                }
                            }
                            finally
                            {
                                Marshal.Release(outputPtr);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapterPtr);
                    }
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HDR detection: DXGI query failed ({ex.Message}).");
        }

        return false;
    }

    private static bool TryGetOutputDesc1(IntPtr outputPtr, out DXGI_OUTPUT_DESC1 outputDesc)
    {
        outputDesc = default;

        try
        {
            var output6Iid = IID_IDXGIOutput6;
            var hr = Marshal.QueryInterface(outputPtr, in output6Iid, out var output6Ptr);
            if (hr < 0 || output6Ptr == IntPtr.Zero)
            {
                Debug.WriteLine($"HDR detection: IDXGIOutput6 query failed with HRESULT 0x{hr:X8}.");
                return false;
            }

            try
            {
                var getDesc1 = GetDxgiMethod<GetOutputDesc1Delegate>(output6Ptr, slot: VtblSlot_IDXGIOutput6_GetDesc1);
                hr = getDesc1(output6Ptr, out outputDesc);
                if (hr < 0)
                    Debug.WriteLine($"HDR detection: IDXGIOutput6.GetDesc1 failed with HRESULT 0x{hr:X8}.");
                return hr >= 0;
            }
            finally
            {
                Marshal.Release(output6Ptr);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HDR detection: output descriptor query failed ({ex.Message}).");
            return false;
        }
    }

    private static bool IsHdrColorSpace(DXGI_COLOR_SPACE_TYPE colorSpace) =>
        colorSpace is DXGI_COLOR_SPACE_TYPE.RGB_FULL_G2084_NONE_P2020
            or DXGI_COLOR_SPACE_TYPE.YCBCR_STUDIO_G2084_LEFT_P2020
            or DXGI_COLOR_SPACE_TYPE.RGB_STUDIO_G2084_NONE_P2020
            or DXGI_COLOR_SPACE_TYPE.YCBCR_STUDIO_G2084_TOPLEFT_P2020
            or DXGI_COLOR_SPACE_TYPE.YCBCR_STUDIO_GHLG_TOPLEFT_P2020;

    private static TDelegate GetDxgiMethod<TDelegate>(IntPtr comPtr, int slot)
        where TDelegate : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comPtr);
        var methodPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
    }

    // ── OpenVINO (via Python import probe) ────────────────────────────────────

    private static (bool hasOv, string? version) DetectOpenVino(Func<string?> findPython)
    {
        var python = findPython();
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

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    // COM vtable slot indices — zero-based position in the virtual function table,
    // counting all inherited slots.  Reference: DXGI interface method ordering in
    // the Windows SDK headers (dxgi.h / dxgi1_6.h).
    //
    //   IDXGIFactory1 : IDXGIFactory(7-11) : IDXGIObject(3-6) : IUnknown(0-2)
    //     slot 12 → IDXGIFactory1::EnumAdapters1
    private const int VtblSlot_IDXGIFactory1_EnumAdapters1 = 12;
    //
    //   IDXGIAdapter : IDXGIObject(3-6) : IUnknown(0-2)
    //     slot  7 → IDXGIAdapter::EnumOutputs
    private const int VtblSlot_IDXGIAdapter_EnumOutputs = 7;
    //
    //   IDXGIOutput6 : IDXGIOutput5(26) : IDXGIOutput4(25) : IDXGIOutput3(24)
    //                : IDXGIOutput2(23) : IDXGIOutput1(19-22) : IDXGIOutput(7-18)
    //                : IDXGIObject(3-6) : IUnknown(0-2)
    //     slot 27 → IDXGIOutput6::GetDesc1
    private const int VtblSlot_IDXGIOutput6_GetDesc1 = 27;

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factoryPtr, uint adapterIndex, out IntPtr adapterPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumOutputsDelegate(IntPtr adapterPtr, uint outputIndex, out IntPtr outputPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetOutputDesc1Delegate(IntPtr output6Ptr, out DXGI_OUTPUT_DESC1 outputDesc);

    private enum DXGI_COLOR_SPACE_TYPE : uint
    {
        RGB_FULL_G2084_NONE_P2020 = 12,
        YCBCR_STUDIO_G2084_LEFT_P2020 = 13,
        RGB_STUDIO_G2084_NONE_P2020 = 14,
        YCBCR_STUDIO_G2084_TOPLEFT_P2020 = 16,
        YCBCR_STUDIO_GHLG_TOPLEFT_P2020 = 18,
    }

    private enum DXGI_MODE_ROTATION : uint
    {
        Unspecified = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGI_OUTPUT_DESC1
    {
        public fixed char DeviceName[32];
        public RECT DesktopCoordinates;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AttachedToDesktop;
        public DXGI_MODE_ROTATION Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public DXGI_COLOR_SPACE_TYPE ColorSpace;
        public fixed float RedPrimary[2];
        public fixed float GreenPrimary[2];
        public fixed float BluePrimary[2];
        public fixed float WhitePoint[2];
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }
}
