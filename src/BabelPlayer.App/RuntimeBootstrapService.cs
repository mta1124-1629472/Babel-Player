using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class RuntimeBootstrapService : IRuntimeBootstrapService
{
    private readonly IBabelLogger _logger;

    public RuntimeBootstrapService(IBabelLogFactory? logFactory = null)
    {
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("runtime.bootstrap");
    }

    public Task<string> EnsureMpvAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => RunInstallAsync("mpv", () => MpvRuntimeInstaller.InstallAsync(onProgress, cancellationToken));

    public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => RunInstallAsync("ffmpeg", () => FfmpegRuntimeInstaller.InstallAsync(onProgress, cancellationToken));

    public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => RunInstallAsync("llama.cpp", () => LlamaCppRuntimeInstaller.InstallAsync(onProgress, cancellationToken));

    private async Task<string> RunInstallAsync(string runtimeName, Func<Task<string>> installAsync)
    {
        _logger.LogInfo("Runtime bootstrap starting.", BabelLogContext.Create(("runtime", runtimeName)));
        try
        {
            var result = await installAsync();
            _logger.LogInfo("Runtime bootstrap completed.", BabelLogContext.Create(("runtime", runtimeName), ("path", result)));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError("Runtime bootstrap failed.", ex, BabelLogContext.Create(("runtime", runtimeName)));
            throw;
        }
    }
}
