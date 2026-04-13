using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;

namespace Babel.Player.ViewModels;

public sealed class EmbeddedPlaybackPipelineViewModel : ViewModelBase, IDisposable
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

    public void Cancel()
    {
        if (_pipelineCts is null)
            return;

        _pipelineCts.Cancel();
        _parent.StatusText = "Canceling pipeline...";
        ResetProgressState();
        _parent.ClearStatusErrorDetail();
    }

    public async Task RunAsync()
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
            await _parent.RefreshSegmentsAsync();
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

    public void Clear()
    {
        _coordinator.ClearPipeline();
        _parent.Segments.Clear();
        _parent.HasSegments = false;
        _parent.ResetInteractiveModes();
        _parent.StatusText = "Pipeline cleared. Ready to run fresh.";
        _parent.ClearStatusErrorDetail();
    }

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

            await _parent.RefreshSegmentsAsync();
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

    public void Dispose()
    {
        _pipelineCts?.Cancel();
        _pipelineCts?.Dispose();
        _pipelineCts = null;

        _diarizationCts?.Cancel();
        _diarizationCts?.Dispose();
        _diarizationCts = null;
    }

    internal void ApplyStageUpdate(SessionWorkflowCoordinator.PipelineStageUpdate update)
    {
        _parent.PipelineStageTitle = $"Stage {update.StageIndex} of {update.StageCount}: {update.Title}";
        _parent.PipelineStageDetail = update.Detail;
        _parent.PipelineProgressPercent = update.Progress01;
        _parent.IsPipelineProgressIndeterminate = update.IsIndeterminate;
        _parent.IsPipelineProgressVisible = true;
    }

    internal void ShowRefreshDetail(string detail)
    {
        if (!_parent.IsPipelineProgressVisible)
            return;

        _parent.PipelineStageDetail = detail;
        _parent.PipelineProgressPercent = 1.0;
        _parent.IsPipelineProgressIndeterminate = true;
    }

    internal void ResetProgressState()
    {
        _parent.PipelineStageTitle = string.Empty;
        _parent.PipelineStageDetail = string.Empty;
        _parent.PipelineProgressPercent = 0;
        _parent.IsPipelineProgressIndeterminate = false;
        _parent.IsPipelineProgressVisible = false;
    }
}
