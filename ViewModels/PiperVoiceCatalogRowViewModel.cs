using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

/// <summary>
/// One Piper voice from <see cref="PiperTtsCatalog"/> with download status for the Speaker Reference Wizard.
/// </summary>
public sealed partial class PiperVoiceCatalogRowViewModel : ViewModelBase
{
    private readonly ModelDownloader _downloader;
    private readonly SessionWorkflowCoordinator _coordinator;
    private readonly PiperVoiceRow _row;
    private readonly Action _onDownloaded;
    private CancellationTokenSource? _cts;

    public PiperVoiceCatalogRowViewModel(
        ModelDownloader downloader,
        SessionWorkflowCoordinator coordinator,
        PiperVoiceRow row,
        Action onDownloaded)
    {
        _downloader = downloader;
        _coordinator = coordinator;
        _row = row;
        _onDownloaded = onDownloaded;
        RefreshStatus();
    }

    public string VoiceId => _row.VoiceId;

    public string Title =>
        string.IsNullOrWhiteSpace(_row.DisplayName) ? _row.VoiceId : $"{_row.DisplayName} ({_row.VoiceId})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(IsProgressVisible))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsProgressVisible => IsDownloading;

    public bool CanDownload => !IsDownloaded && !IsDownloading;

    public bool CanCancel => IsDownloading;

    public string StatusLabel => IsDownloaded ? "✓ Ready"
        : IsDownloading ? "Downloading…"
        : ErrorMessage != null ? $"Failed: {ErrorMessage}"
        : "⬇ Needs Download";

    public void RefreshStatus()
    {
        IsDownloaded = ModelDownloader.IsPiperVoiceDownloaded(_row.VoiceId, _coordinator.CurrentSettings.PiperModelDir);
        ErrorMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        ErrorMessage = null;
        IsDownloading = true;
        DownloadProgress = 0;
        _cts = new CancellationTokenSource();

        try
        {
            bool ok = await _downloader.DownloadPiperVoiceAsync(
                _row.VoiceId,
                _coordinator.CurrentSettings.PiperModelDir,
                new Progress<double>(p => Dispatcher.UIThread.Post(() => DownloadProgress = p)),
                _cts.Token);
            RefreshStatus();
            if (ok || IsDownloaded)
                _onDownloaded();
            if (!ok && !IsDownloaded)
                ErrorMessage = "Download failed. Check logs.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsDownloading = false;
            DownloadProgress = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();
}
