using System.Diagnostics;
using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.Infrastructure;

public sealed class DefaultRuntimeProvisioner : IRuntimeProvisioner
{
    private readonly IRuntimeBootstrapService _runtimeBootstrapService;
    private readonly CredentialFacade? _credentialFacade;
    private readonly ICredentialDialogService? _credentialDialogService;
    private readonly IFilePickerService? _filePickerService;
    private readonly Func<string, string?> _environmentVariableReader;
    private readonly IBabelLogger _logger;

    public DefaultRuntimeProvisioner(
        IRuntimeBootstrapService runtimeBootstrapService,
        CredentialFacade? credentialFacade = null,
        ICredentialDialogService? credentialDialogService = null,
        IFilePickerService? filePickerService = null,
        Func<string, string?>? environmentVariableReader = null,
        IBabelLogFactory? logFactory = null)
    {
        _runtimeBootstrapService = runtimeBootstrapService;
        _credentialFacade = credentialFacade;
        _credentialDialogService = credentialDialogService;
        _filePickerService = filePickerService;
        _environmentVariableReader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("runtime.provisioner");
    }

    public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => _runtimeBootstrapService.EnsureLlamaCppAsync(onProgress, cancellationToken);

    public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => _runtimeBootstrapService.EnsureFfmpegAsync(onProgress, cancellationToken);

    public async Task<bool> EnsureLlamaCppRuntimeReadyAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        if (TryResolveLlamaCppServerPath() is not null)
        {
            _logger.LogInfo("llama.cpp runtime already available.");
            return true;
        }

        if (_credentialDialogService is null)
        {
            return false;
        }

        var choice = await _credentialDialogService.PromptForLlamaCppBootstrapChoiceAsync(
            "llama.cpp Setup",
            "Local HY-MT translation needs llama-server. Install it automatically or choose an existing executable.",
            cancellationToken);
        _logger.LogInfo("llama.cpp bootstrap choice received.", BabelLogContext.Create(("choice", choice)));

        switch (choice)
        {
            case LlamaCppBootstrapChoice.InstallAutomatically:
                {
                    var serverPath = await _runtimeBootstrapService.EnsureLlamaCppAsync(onProgress, cancellationToken);
                    if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
                    {
                        return false;
                    }

                    _credentialFacade?.SaveLlamaCppServerPath(serverPath);
                    _credentialFacade?.SaveLlamaCppRuntimeVersion(LlamaCppRuntimeInstaller.RuntimeVersion);
                    _credentialFacade?.SaveLlamaCppRuntimeSource(LlamaCppRuntimeInstaller.RuntimeSource);
                    _logger.LogInfo("llama.cpp runtime installed automatically.", BabelLogContext.Create(("serverPath", serverPath)));
                    return true;
                }
            case LlamaCppBootstrapChoice.ChooseExisting:
                {
                    if (_filePickerService is null)
                    {
                        return false;
                    }

                    var selectedPath = await _filePickerService.PickExecutableAsync(
                        "Choose llama-server",
                        "llama.cpp server",
                        [".exe"],
                        cancellationToken);
                    if (string.IsNullOrWhiteSpace(selectedPath) || !File.Exists(selectedPath))
                    {
                        return false;
                    }

                    _credentialFacade?.SaveLlamaCppServerPath(selectedPath);
                    _credentialFacade?.SaveLlamaCppRuntimeSource("manual");
                    _logger.LogInfo("llama.cpp runtime path selected manually.", BabelLogContext.Create(("serverPath", selectedPath)));
                    return true;
                }
            case LlamaCppBootstrapChoice.OpenOfficialDownloadPage:
                _logger.LogInfo("Opening llama.cpp download page.");
                Process.Start(new ProcessStartInfo
                {
                    FileName = LlamaCppRuntimeInstaller.ReleasePageUrl,
                    UseShellExecute = true
                });
                return false;
            default:
                return false;
        }
    }

    private string? TryResolveLlamaCppServerPath()
    {
        var configuredPath = _environmentVariableReader("LLAMA_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        configuredPath = _credentialFacade?.GetLlamaCppServerPath();
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var installedPath = LlamaCppRuntimeInstaller.GetInstalledServerPath();
        return File.Exists(installedPath) ? installedPath : null;
    }
}
