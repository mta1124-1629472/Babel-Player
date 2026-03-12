using BabelPlayer.Core;

namespace BabelPlayer.App;
using System.Diagnostics;

public sealed partial class SubtitleApplicationService
{
    private void HandleRecognizedChunk(TranscriptChunk chunk, int generationId)
    {
        if (generationId != _workflowStateStore.Snapshot.ActiveCaptionGenerationId || string.IsNullOrWhiteSpace(chunk.Text))
        {
            return;
        }

        var cue = new SubtitleCue
        {
            Start = TimeSpan.FromSeconds(chunk.StartTimeSec),
            End = TimeSpan.FromSeconds(chunk.EndTimeSec),
            SourceText = chunk.Text.Trim(),
            SourceLanguage = SubtitleCueSessionMapper.ResolveSourceLanguage(chunk.Text, DefaultSourceLanguage)
        };
        var activeModelKey = _workflowStateStore.Snapshot.ActiveCaptionGenerationModelKey ?? _workflowStateStore.Snapshot.SelectedTranscriptionModelKey;
        var transcriptionModel = SubtitleWorkflowCatalog.GetTranscriptionModel(activeModelKey);
        var transcriptSegment = SubtitleCueSessionMapper.BuildTranscriptSegment(
            cue,
            SubtitlePipelineSource.Generated,
            _workflowStateStore.Snapshot.CurrentVideoPath,
            activeModelKey,
            transcriptionModel.DisplayName,
            DefaultSourceLanguage);
        var currentSourceLanguage = SubtitleCueSessionMapper.ResolveAggregateSourceLanguage(
            _mediaSessionCoordinator.Snapshot.LanguageAnalysis.CurrentSourceLanguage,
            cue.SourceLanguage,
            DefaultSourceLanguage);

        UpdateWorkflowState(state => state with
        {
            OverlayStatus = null
        });
        _mediaSessionCoordinator.SetCaptionGenerationState(true);
        _mediaSessionCoordinator.UpsertTranscriptSegment(transcriptSegment, currentSourceLanguage);
        ApplyAutomaticTranslationPreferenceIfNeeded();

        if (!string.IsNullOrWhiteSpace(_workflowStateStore.Snapshot.CurrentVideoPath))
        {
            CacheGeneratedSubtitles(_workflowStateStore.Snapshot.CurrentVideoPath!, activeModelKey, CurrentCues);
        }

        PublishStatus(
            $"Generating captions ({_workflowStateStore.Snapshot.CaptionGenerationModeLabel})... captions ready through {SubtitleCueSessionMapper.FormatClock(cue.End)}.");
        _ = TranslateCueAsync(transcriptSegment, _captionGenerationCts?.Token ?? CancellationToken.None);
    }

    private async Task TranslateAllCuesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var selectionKey = _workflowStateStore.Snapshot.SelectedTranslationModelKey;
            var selection = SubtitleWorkflowCatalog.GetTranslationModel(selectionKey);
            var cues = _mediaSessionCoordinator.Snapshot.Transcript.Segments.ToList();
            var isTranslationEnabled = _mediaSessionCoordinator.Snapshot.Translation.IsEnabled;
            using var activity = BabelTracing.Source.StartActivity("subtitle.translate_all");
            activity?.SetTag(BabelTracing.Tags.ModelKey, selection.Key);
            activity?.SetTag(BabelTracing.Tags.CueCount, cues.Count);
            _logger.LogInfo("Translating cues.", BabelLogContext.Create(("modelKey", selection.Key), ("cueCount", cues.Count), ("translationEnabled", isTranslationEnabled)));

            if (isTranslationEnabled
                && SubtitleWorkflowCatalog.IsCloudTranslationProvider(selection.Provider))
            {
                var translatedTexts = await TranslateCueBatchAsync(selection, cues, cancellationToken);
                for (var index = 0; index < cues.Count; index++)
                {
                    _mediaSessionCoordinator.UpsertTranslationSegment(CreateTranslationSegment(cues[index], translatedTexts[index]));
                }
            }
            else
            {
                foreach (var cue in cues)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await TranslateCueAsync(cue, cancellationToken);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                PublishStatus(isTranslationEnabled
                    ? $"Prepared {CurrentCues.Count} translated subtitle cues."
                    : $"Prepared {CurrentCues.Count} source-language subtitle cues.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await HandleCloudServiceFailureAsync(ex);
        }
    }

    private async Task TranslateCueAsync(TranscriptSegment cue, CancellationToken cancellationToken)
    {
        if (SubtitleCueSessionMapper.HasTranslatedSegment(cue, _mediaSessionCoordinator.Snapshot))
        {
            return;
        }

        if (ShouldUseTranscriptDirectly(cue))
        {
            _mediaSessionCoordinator.UpsertTranslationSegment(CreateTranslationSegment(cue, cue.Text.Trim()));
            return;
        }

        lock (_translationSync)
        {
            if (SubtitleCueSessionMapper.HasTranslatedSegment(cue, _mediaSessionCoordinator.Snapshot) || !_inFlightCueTranslations.Add(cue.Id.Value))
            {
                return;
            }
        }

        try
        {
            PublishLocalTranslationPreparationStatus();
            var selection = SubtitleWorkflowCatalog.GetTranslationModel(_workflowStateStore.Snapshot.SelectedTranslationModelKey);
            var translated = await _subtitleTranslator.TranslateAsync(selection, cue.Text, cancellationToken);
            _mediaSessionCoordinator.UpsertTranslationSegment(CreateTranslationSegment(cue, translated));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await HandleCloudServiceFailureAsync(ex);
        }
        finally
        {
            lock (_translationSync)
            {
                _inFlightCueTranslations.Remove(cue.Id.Value);
            }
        }
    }

    private bool ShouldUseTranscriptDirectly(TranscriptSegment cue)
    {
        var state = _workflowStateStore.Snapshot;
        var isTranslationEnabled = _mediaSessionCoordinator.Snapshot.Translation.IsEnabled;
        if (!isTranslationEnabled || string.IsNullOrWhiteSpace(state.SelectedTranslationModelKey))
        {
            return true;
        }

        return SubtitleCueSessionMapper.IsLanguageCode(cue.Language, _translationTargetLanguage);
    }

    private async Task<IReadOnlyList<string>> TranslateCueBatchAsync(
        TranslationModelSelection selection,
        IReadOnlyList<TranscriptSegment> cues,
        CancellationToken cancellationToken)
    {
        const int batchSize = 20;
        var translated = new List<string>(cues.Count);
        for (var index = 0; index < cues.Count; index += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = cues.Skip(index).Take(batchSize).ToList();
            var batchTranslations = await _subtitleTranslator.TranslateBatchAsync(
                selection,
                batch.Select(cue => cue.Text).ToArray(),
                cancellationToken);
            translated.AddRange(batchTranslations);
        }

        return translated;
    }

    private async Task HandleCloudServiceFailureAsync(Exception ex)
    {
        _logger.LogError("Subtitle cloud workflow failed.", ex, BabelLogContext.Create(("translationModel", _workflowStateStore.Snapshot.SelectedTranslationModelKey), ("transcriptionModel", _workflowStateStore.Snapshot.SelectedTranscriptionModelKey)));
        if (SubtitleCueSessionMapper.ShouldDisableCloudForError(ex))
        {
            var state = _workflowStateStore.Snapshot;
            if (SubtitleWorkflowCatalog.IsCloudTranslationProvider(SubtitleWorkflowCatalog.GetTranslationModel(state.SelectedTranslationModelKey).Provider))
            {
                RestoreTranslationSelection(null);
            }

            if (SubtitleWorkflowCatalog.GetTranscriptionModel(state.SelectedTranscriptionModelKey).Provider == TranscriptionProvider.Cloud)
            {
                _credentialFacade.SaveSubtitleModelKey(SubtitleWorkflowCatalog.DefaultTranscriptionModelKey);
                UpdateWorkflowState(current => current with
                {
                    SelectedTranscriptionModelKey = SubtitleWorkflowCatalog.DefaultTranscriptionModelKey,
                    CaptionGenerationModeLabel = SubtitleWorkflowCatalog.GetTranscriptionModel(SubtitleWorkflowCatalog.DefaultTranscriptionModelKey).DisplayName
                });
            }

            PublishStatus("Cloud models were disabled after a quota or rate-limit error.");
            return;
        }

        PublishStatus(ex.Message);
        await Task.CompletedTask;
    }

    private async Task<bool> WarmupSelectedLocalTranslationRuntimeAsync(TranslationModelSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            PublishStatus($"Preparing {selection.DisplayName}.", $"Preparing {selection.DisplayName}.");
            await _subtitleTranslator.WarmupAsync(selection, cancellationToken);
            _logger.LogInfo("Local translation runtime warmed up.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider)));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("Local translation runtime warmup failed.", ex, BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider)));
            PublishStatus(ex.Message, "Local translation model setup failed.");
            return false;
        }
    }

    private void RestoreTranslationSelection(string? previousModelKey)
    {
        UpdateWorkflowState(state => state with
        {
            SelectedTranslationModelKey = previousModelKey
        });

        if (string.IsNullOrWhiteSpace(previousModelKey))
        {
            _credentialFacade.ClearTranslationModelKey();
        }
        else
        {
            _credentialFacade.SaveTranslationModelKey(previousModelKey);
        }
    }

    private void ResetCurrentTranslations()
    {
        lock (_translationSync)
        {
            _inFlightCueTranslations.Clear();
        }

        _mediaSessionCoordinator.ClearTranslations();
    }

    private void CancelTranslationWork()
    {
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = null;
    }

    private void CancelCaptionGeneration()
    {
        _captionGenerationCts?.Cancel();
        _captionGenerationCts?.Dispose();
        _captionGenerationCts = null;
        _mediaSessionCoordinator.SetCaptionGenerationState(false);
        UpdateWorkflowState(state => state with
        {
            ActiveCaptionGenerationModelKey = null
        });
    }

    private void PublishLocalTranslationPreparationStatus()
    {
        var selection = SubtitleWorkflowCatalog.GetTranslationModel(_workflowStateStore.Snapshot.SelectedTranslationModelKey);
        string? message = selection.Provider switch
        {
            TranslationProvider.LocalHyMt15_1_8B => "Starting HY-MT1.5 1.8B local translation. First use may download and load the model through llama.cpp.",
            TranslationProvider.LocalHyMt15_7B => "Starting HY-MT1.5 7B local translation. First use may download and load the model through llama.cpp.",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(_workflowStateStore.Snapshot.OverlayStatus))
        {
            PublishStatus(message, message);
        }
    }

    private void HandleSubtitleModelTransferProgress(ModelTransferProgress progress, int generationId)
    {
        if (generationId != _workflowStateStore.Snapshot.ActiveCaptionGenerationId)
        {
            return;
        }

        var message = SubtitleCueSessionMapper.FormatSubtitleModelTransferStatus(progress);
        PublishStatus(message, message);
    }

    private void HandleLocalTranslationRuntimeStatus(LocalTranslationRuntimeStatus status)
    {
        PublishStatus(status.Message, status.Message);
    }

    private void HandleLlamaRuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        RuntimeInstallProgressChanged?.Invoke(progress);
        PublishStatus(progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading llama.cpp runtime... {ratio:P0}."
                : "Downloading llama.cpp runtime...",
            "extracting" => progress.ProgressRatio is double ratio
                ? $"Extracting llama.cpp runtime... {ratio:P0}."
                : "Extracting llama.cpp runtime...",
            "ready" => "llama.cpp runtime is ready.",
            _ => "Preparing llama.cpp runtime..."
        }, progress.Stage == "ready" ? "llama.cpp runtime is ready." : null);
    }

    private void HandleFfmpegRuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        RuntimeInstallProgressChanged?.Invoke(progress);
        PublishStatus(progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading ffmpeg runtime... {ratio:P0}."
                : "Downloading ffmpeg runtime...",
            "extracting" => progress.ProgressRatio is double ratio
                ? $"Extracting ffmpeg runtime... {ratio:P0}."
                : "Extracting ffmpeg runtime...",
            "ready" => "ffmpeg runtime is ready.",
            _ => "Preparing ffmpeg runtime..."
        }, progress.Stage == "ready" ? "ffmpeg runtime is ready." : null);
    }

    private void PublishStatus(string message, string? overlayStatus = null)
    {
        if (overlayStatus is not null)
        {
            UpdateWorkflowState(state => state with
            {
                OverlayStatus = overlayStatus
            });
            _mediaSessionCoordinator.SetSubtitleStatus(overlayStatus);
        }

        _logger.LogInfo("Subtitle workflow status updated.", BabelLogContext.Create(("message", message), ("overlayStatus", overlayStatus)));
        StatusChanged?.Invoke(message);
    }

    private void UpdateWorkflowState(Func<SubtitleWorkflowState, SubtitleWorkflowState> update)
    {
        _workflowStateStore.Update(update);
    }
}
