using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

public sealed partial class EmbeddedPlaybackPipelineViewModel : ViewModelBase, IDisposable
{
    private readonly EmbeddedPlaybackViewModel _parent;
    private readonly SessionWorkflowCoordinator _coordinator;
    private CancellationTokenSource? _pipelineCts;
    private CancellationTokenSource? _diarizationCts;

    internal EmbeddedPlaybackPipelineViewModel(
        EmbeddedPlaybackViewModel parent,
        SessionWorkflowCoordinator coordinator)
    {
        _parent = parent;
        _coordinator = coordinator;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PipelineProgressStatusLine))]
    private double _pipelineProgressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PipelineProgressStatusLine))]
    private bool _isPipelineProgressVisible;

    [ObservableProperty]
    private string _pipelineStageTitle = string.Empty;

    [ObservableProperty]
    private string _pipelineStageDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PipelineProgressStatusLine))]
    private bool _isPipelineProgressIndeterminate;

    public bool CanRunPipeline => !_parent.IsBusy;

    public bool CanRunDiarizationOnly =>
        !_parent.IsBusy &&
        _coordinator.CurrentSession.Stage >= SessionWorkflowStage.Transcribed &&
        !string.IsNullOrWhiteSpace(_parent.SpeakerRouting.DiarizationProvider);

    public string PipelineProgressStatusLine =>
        !IsPipelineProgressVisible
            ? string.Empty
            : IsPipelineProgressIndeterminate
                ? "Current stage progress is active, but this provider has not reported a numeric percentage yet."
                : $"Current stage progress: {PipelineProgressPercent:P0}. The bar resets for each remaining pipeline stage.";

    [RelayCommand(CanExecute = nameof(CanRunPipeline))]
    public async Task RunPipelineAsync()
    {
        var diagnostics = _coordinator.BootstrapDiagnostics;
        if (!diagnostics.AllDependenciesAvailable)
        {
            _parent.StatusText = $"⚠ {diagnostics.DiagnosticSummary}";
            _parent.ClearStatusErrorDetail();
            return;
        }

        _pipelineCts?.Cancel();
        _pipelineCts?.Dispose();
        _pipelineCts = new CancellationTokenSource();
        var cancellationToken = _pipelineCts.Token;
        ResetProgressState();
        var stageProgress = new Progress<SessionWorkflowCoordinator.PipelineStageUpdate>(ApplyStageUpdate);

        try
        {
            _parent.IsBusy = true;
            _parent.StatusText = "Running pipeline…";
            _parent.ClearStatusErrorDetail();

            if (_coordinator.CurrentSession.Stage == SessionWorkflowStage.Diarized)
            {
                await _coordinator.ContinuePipelineAsync(
                    progress: null,
                    stageProgress: stageProgress,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await _coordinator.AdvancePipelineAsync(
                    progress: null,
                    stageProgress: stageProgress,
                    cancellationToken: cancellationToken);
            }

            ShowRefreshDetail("Loading segments and refreshing playback data…");
            _parent.StatusText = "Loading segments…";
            await _parent.Preview.RefreshSegmentsAsync();
            _parent.StatusText = _coordinator.CurrentSession.StatusMessage;
            _parent.ClearStatusErrorDetail();
        }
        catch (OperationCanceledException)
        {
            _parent.StatusText = "Pipeline cancelled.";
            _parent.ClearStatusErrorDetail();
        }
        catch (Exception ex)
        {
            _parent.StatusText = $"Pipeline failed: {ex.Message}";
            _parent.SetStatusErrorDetail("Pipeline failed", ex);
        }
        finally
        {
            _parent.IsBusy = false;
            ResetProgressState();
            _pipelineCts?.Dispose();
            _pipelineCts = null;
        }
    }

    [RelayCommand]
    public void ClearPipeline()
    {
        _coordinator.ClearPipeline();
        _parent.Preview.ClearSegments();
        _parent.ResetInteractiveModes();
        _parent.StatusText = "Pipeline cleared. Ready to run fresh.";
        _parent.ClearStatusErrorDetail();
    }

    [RelayCommand]
    public void CancelPipeline()
    {
        if (_pipelineCts is null)
            return;

        _pipelineCts.Cancel();
        _parent.StatusText = "Canceling pipeline...";
        ResetProgressState();
        _parent.ClearStatusErrorDetail();
    }

    [RelayCommand(CanExecute = nameof(CanRunDiarizationOnly))]
    public async Task RunDiarizationOnlyAsync()
    {
        _diarizationCts?.Cancel();
        _diarizationCts?.Dispose();
        _diarizationCts = new CancellationTokenSource();
        var cancellationToken = _diarizationCts.Token;

        try
        {
            _parent.IsBusy = true;
            _parent.StatusText = $"Running {_parent.ResolveDiarizationProviderLabel()} diarization…";
            _parent.ClearStatusErrorDetail();

            var hadTranslatableOutput = _coordinator.CurrentSession.Stage >= SessionWorkflowStage.Translated;
            var speakerAssignmentsChanged = await _coordinator.RunDiarizationAsync(cancellationToken);
            string completionStatus;

            if (speakerAssignmentsChanged && hadTranslatableOutput)
            {
                _coordinator.ResetPipelineToTranslated();
                completionStatus = "Diarization updated speaker assignments. TTS output was reset to translated state.";
            }
            else if (speakerAssignmentsChanged)
            {
                completionStatus = "Diarization updated speaker assignments.";
            }
            else
            {
                completionStatus = "Diarization complete. Speaker assignments were unchanged.";
            }

            await _parent.Preview.RefreshSegmentsAsync();
            _parent.StatusText = completionStatus;
            _parent.ClearStatusErrorDetail();
        }
        catch (OperationCanceledException)
        {
            _parent.StatusText = "Re-diarize cancelled.";
            _parent.ClearStatusErrorDetail();
        }
        catch (Exception ex)
        {
            _parent.StatusText = $"Re-diarize failed: {ex.Message}";
            _parent.SetStatusErrorDetail("Re-diarize failed", ex);
        }
        finally
        {
            _parent.IsBusy = false;
            _diarizationCts?.Dispose();
            _diarizationCts = null;
        }
    }

    public void NotifyBusyStateChanged()
    {
        RunPipelineCommand.NotifyCanExecuteChanged();
        RunDiarizationOnlyCommand.NotifyCanExecuteChanged();
    }

    public void NotifySessionStateChanged() => RunDiarizationOnlyCommand.NotifyCanExecuteChanged();

    internal void ApplyStageUpdate(SessionWorkflowCoordinator.PipelineStageUpdate update)
    {
        PipelineStageTitle = $"Stage {update.StageIndex} of {update.StageCount}: {update.Title}";
        PipelineStageDetail = update.Detail;
        PipelineProgressPercent = update.Progress01;
        IsPipelineProgressIndeterminate = update.IsIndeterminate;
        IsPipelineProgressVisible = true;
    }

    internal void ShowRefreshDetail(string detail)
    {
        if (!IsPipelineProgressVisible)
            return;

        PipelineStageDetail = detail;
        PipelineProgressPercent = 1.0;
        IsPipelineProgressIndeterminate = true;
    }

    internal void ResetProgressState()
    {
        PipelineStageTitle = string.Empty;
        PipelineStageDetail = string.Empty;
        PipelineProgressPercent = 0;
        IsPipelineProgressIndeterminate = false;
        IsPipelineProgressVisible = false;
    }

    public void Dispose()
    {
        _pipelineCts?.Cancel();
        _pipelineCts?.Dispose();
        _pipelineCts = null;

        _diarizationCts?.Cancel();
        _diarizationCts?.Dispose();
        _diarizationCts = null;
    }
}
