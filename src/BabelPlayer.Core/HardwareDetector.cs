using System.Management;
using System.Runtime.InteropServices;

namespace BabelPlayer.Core;

public static class HardwareDetector
{
    public static string GetSummary()
    {
        var gpuInfo = GetGpuSummary();
        var cudaInfo = GetCudaSummary(gpuInfo.HasNvidiaGpu);

        if (cudaInfo.IsAvailable && !string.IsNullOrWhiteSpace(gpuInfo.PreferredGpuName))
        {
            return $"Accelerator: {gpuInfo.PreferredGpuName} (CUDA)";
        }

        if (gpuInfo.HasDedicatedGpu && !string.IsNullOrWhiteSpace(gpuInfo.PreferredGpuName))
        {
            return $"Accelerator: {gpuInfo.PreferredGpuName}";
        }

        return "Accelerator: CPU";
    }

    private static (string PreferredGpuName, bool HasDedicatedGpu, bool HasNvidiaGpu) GetGpuSummary()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            using var results = searcher.Get();

            var gpus = new List<string>();
            var hasNvidiaGpu = false;

            foreach (var item in results.Cast<ManagementObject>())
            {
                var name = item["Name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    hasNvidiaGpu = true;
                }

                gpus.Add(name);
            }

            if (gpus.Count == 0)
            {
                return (string.Empty, false, false);
            }

            var preferredGpu = gpus.FirstOrDefault(gpu => gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                ?? gpus.FirstOrDefault(gpu =>
                    gpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    gpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                    gpu.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                    gpu.Contains("GTX", StringComparison.OrdinalIgnoreCase))
                ?? gpus[0];

            var hasDedicatedGpu = gpus.Any(gpu =>
                gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                gpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                gpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                gpu.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                gpu.Contains("GTX", StringComparison.OrdinalIgnoreCase));

            return (preferredGpu, hasDedicatedGpu, hasNvidiaGpu);
        }
        catch
        {
            return (string.Empty, false, false);
        }
    }

    private static (string Summary, bool IsAvailable) GetCudaSummary(bool hasNvidiaGpu)
    {
        if (!hasNvidiaGpu)
        {
            return ("No NVIDIA GPU detected", false);
        }

        var driverLoaded = NativeLibrary.TryLoad("nvcuda.dll", out var cudaHandle);
        if (driverLoaded)
        {
            NativeLibrary.Free(cudaHandle);
        }

        var toolkitPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        var nvccInPath = FindOnPath("nvcc.exe");

        if (driverLoaded && !string.IsNullOrWhiteSpace(toolkitPath))
        {
            return ($"Driver OK, Toolkit {toolkitPath}", true);
        }

        if (driverLoaded && !string.IsNullOrWhiteSpace(nvccInPath))
        {
            return ($"Driver OK, nvcc at {nvccInPath}", true);
        }

        if (driverLoaded)
        {
            return ("Driver available", true);
        }

        return ("NVIDIA GPU present, CUDA driver not found", false);
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var path in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(path, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }
}
