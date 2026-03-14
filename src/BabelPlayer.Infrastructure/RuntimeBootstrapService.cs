using BabelPlayer.App;
using BabelPlayer.Core;
using System.Runtime.InteropServices;

namespace BabelPlayer.Infrastructure;

public sealed class RuntimeBootstrapService : IRuntimeBootstrapService
{
    private readonly IBabelLogger _logger;

    public RuntimeBootstrapService(IBabelLogFactory? logFactory = null)
    {
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("runtime.bootstrap");
    }

    public Task<string> EnsureMpvAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => RunInstallAsync("mpv", architecture => MpvRuntimeInstaller.InstallAsync(architecture, onProgress, cancellationToken));

    public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => RunInstallAsync("ffmpeg", architecture => FfmpegRuntimeInstaller.InstallAsync(architecture, onProgress, cancellationToken));

    public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => RunInstallAsync("llama.cpp", architecture => LlamaCppRuntimeInstaller.InstallAsync(architecture, onProgress, cancellationToken));

    private async Task<string> RunInstallAsync(string runtimeName, Func<Architecture, Task<string>> installAsync)
    {
        var architecture = RuntimeArchitectureHelper.GetCurrentArchitecture();
        var runtimeId = RuntimeArchitectureHelper.ToRuntimeIdentifier(architecture);
        _logger.LogInfo("Runtime bootstrap starting.", BabelLogContext.Create(("runtime", runtimeName)));
        try
        {
            var result = await installAsync(architecture);
            _logger.LogInfo("Runtime bootstrap completed.", BabelLogContext.Create(("runtime", runtimeName), ("runtimeId", runtimeId), ("path", result)));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError("Runtime bootstrap failed.", ex, BabelLogContext.Create(("runtime", runtimeName), ("runtimeId", runtimeId)));
            throw;
        }
    }
}
