namespace BabelPlayer.App;

public sealed class RuntimeBootstrapService : IRuntimeBootstrapService
{
    public Task<string> EnsureMpvAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => MpvRuntimeInstaller.InstallAsync(onProgress, cancellationToken);

    public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => FfmpegRuntimeInstaller.InstallAsync(onProgress, cancellationToken);

    public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => LlamaCppRuntimeInstaller.InstallAsync(onProgress, cancellationToken);
}
