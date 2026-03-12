using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed partial class SubtitleApplicationService
{
    private void LoadPersistedSelections()
    {
        var transcriptionKey = _providerAvailabilityService.ResolvePersistedTranscriptionModelKey(_credentialFacade.GetSubtitleModelKey());
        var translationKey = _providerAvailabilityService.ResolvePersistedTranslationModelKey(_credentialFacade.GetTranslationModelKey());
        var autoTranslateEnabled = _credentialFacade.GetAutoTranslateEnabled();

        UpdateWorkflowState(state => state with
        {
            SelectedTranscriptionModelKey = transcriptionKey,
            SelectedTranslationModelKey = translationKey,
            AutoTranslateEnabled = autoTranslateEnabled,
            CaptionGenerationModeLabel = SubtitleWorkflowCatalog.GetTranscriptionModel(transcriptionKey).DisplayName
        });
        _mediaSessionCoordinator.SetTranslationState(_workflowStateStore.Snapshot.IsTranslationEnabled, autoTranslateEnabled);
    }

    private async Task<bool> EnsureTranslationProviderReadyAsync(TranslationModelSelection selection, CancellationToken cancellationToken)
    {
        _logger.LogInfo("Ensuring translation provider is ready.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider)));
        if (selection.Provider is not (TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B))
        {
            return await _aiCredentialCoordinator.EnsureTranslationProviderCredentialsAsync(selection.Provider, cancellationToken);
        }

        if (_providerAvailabilityService.ResolveLlamaCppServerPath() is null
            && !await _runtimeProvisioner.EnsureLlamaCppRuntimeReadyAsync(HandleLlamaRuntimeInstallProgress, cancellationToken))
        {
            return false;
        }

        return await WarmupSelectedLocalTranslationRuntimeAsync(selection, cancellationToken);
    }

    private void InitializeTranslationPreferencesForNewVideo()
    {
        UpdateWorkflowState(state => state with
        {
            CurrentVideoTranslationPreferenceLocked = false
        });
        SetTranslationEnabledForCurrentVideo(false);
    }

    private void ApplyAutomaticTranslationPreferenceIfNeeded()
    {
        var state = _workflowStateStore.Snapshot;
        if (state.CurrentVideoTranslationPreferenceLocked)
        {
            return;
        }

        if (!state.AutoTranslateEnabled || string.IsNullOrWhiteSpace(state.SelectedTranslationModelKey))
        {
            SetTranslationEnabledForCurrentVideo(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(state.CurrentSourceLanguage)
            || string.Equals(state.CurrentSourceLanguage, DefaultSourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetTranslationEnabledForCurrentVideo(!IsLanguageCode(state.CurrentSourceLanguage, _autoTranslatePreferredSourceLanguage));
    }

    private void SetTranslationEnabledForCurrentVideo(bool enabled)
    {
        UpdateWorkflowState(state => state with
        {
            IsTranslationEnabled = enabled
        });
        _mediaSessionCoordinator.SetTranslationState(enabled, _workflowStateStore.Snapshot.AutoTranslateEnabled);
    }

    private async Task ReprocessCurrentSubtitlesForTranslationSettingsAsync(CancellationToken cancellationToken)
    {
        var state = _workflowStateStore.Snapshot;
        _logger.LogInfo("Reprocessing subtitles for translation settings.", BabelLogContext.Create(("translationEnabled", state.IsTranslationEnabled), ("modelKey", state.SelectedTranslationModelKey)));
        if (state.IsTranslationEnabled && string.IsNullOrWhiteSpace(state.SelectedTranslationModelKey))
        {
            ResetCurrentTranslations();
            PublishStatus("Select a translation model to start translating this video.");
            return;
        }

        if (!HasCurrentCues)
        {
            return;
        }

        ResetCurrentTranslations();
        PublishStatus(state.IsTranslationEnabled
            ? "Updating subtitle translation for the current video."
            : "Translation disabled for the current video.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = cts;
        _ = TranslateAllCuesAsync(cts.Token);
    }

    private async Task ReprocessCurrentSubtitlesForTranscriptionModelAsync(
        TranscriptionModelSelection selection,
        CancellationToken cancellationToken,
        bool suppressStatus)
    {
        _logger.LogInfo("Reprocessing subtitles for transcription model.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider), ("suppressStatus", suppressStatus)));
        var session = _mediaSessionCoordinator.Snapshot;
        var currentVideoPath = _workflowStateStore.Snapshot.CurrentVideoPath;
        if (session.Transcript.Source != SubtitlePipelineSource.Generated || string.IsNullOrWhiteSpace(currentVideoPath))
        {
            if (!suppressStatus)
            {
                PublishStatus($"Selected transcription model: {selection.DisplayName}.");
            }

            return;
        }

        if (TryLoadCachedGeneratedSubtitles(currentVideoPath, selection.Key))
        {
            if (!suppressStatus)
            {
                PublishStatus($"Loaded cached captions for {selection.DisplayName}.");
            }

            await LoadSubtitleCuesAsync(
                CurrentCues,
                SubtitlePipelineSource.Generated,
                $"Loaded cached generated captions ({selection.DisplayName})",
                cancellationToken,
                preserveCurrentTranslationPreference: true);
            return;
        }

        if (!suppressStatus)
        {
            PublishStatus($"Restarting transcription with {selection.DisplayName}.");
        }

        await StartAutomaticCaptionGenerationAsync(currentVideoPath, cancellationToken, preserveCurrentTranslationPreference: true);
    }

    private async Task<SubtitleLoadResult> LoadSubtitleCuesAsync(
        IReadOnlyList<SubtitleCue> cues,
        SubtitlePipelineSource source,
        string statusPrefix,
        CancellationToken cancellationToken,
        bool preserveCurrentTranslationPreference = false)
    {
        _logger.LogInfo("Loading subtitle cues into workflow.", BabelLogContext.Create(("source", source), ("cueCount", cues.Count), ("preserveTranslationPreference", preserveCurrentTranslationPreference)));
        var currentVideoPath = _workflowStateStore.Snapshot.CurrentVideoPath;
        CancelCaptionGeneration();
        CancelTranslationWork();
        if (!preserveCurrentTranslationPreference)
        {
            InitializeTranslationPreferencesForNewVideo();
        }

        UpdateWorkflowState(state => state with
        {
            OverlayStatus = null
        });

        var projectedCues = CloneCues(cues);
        lock (_translationSync)
        {
            _inFlightCueTranslations.Clear();
        }

        if (projectedCues.Count == 0)
        {
            UpdateWorkflowState(state => state with
            {
                CurrentSourceLanguage = DefaultSourceLanguage,
                OverlayStatus = "Loaded subtitle file contains no playable cues."
            });
            _mediaSessionCoordinator.ClearTranscriptSegments(source, false, "Loaded subtitle file contains no playable cues.", DefaultSourceLanguage);
            PublishStatus("No playable subtitle cues were found.", "Loaded subtitle file contains no playable cues.");
            return new SubtitleLoadResult(source, 0, source == SubtitlePipelineSource.Sidecar, false);
        }

        var currentSourceLanguage = ApplySourceLanguageToCues(projectedCues);
        UpdateWorkflowState(state => state with
        {
            CurrentSourceLanguage = currentSourceLanguage
        });
        _mediaSessionCoordinator.SetTranscriptSegments(
            BuildTranscriptSegments(projectedCues, source, currentVideoPath),
            source,
            currentSourceLanguage);
        ApplyAutomaticTranslationPreferenceIfNeeded();

        PublishStatus(
            $"{statusPrefix} ({projectedCues.Count} cues).",
            _workflowStateStore.Snapshot.IsTranslationEnabled
                ? "Preparing translated subtitles..."
                : "Preparing source-language subtitles...");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = cts;
        _ = TranslateAllCuesAsync(cts.Token);

        return new SubtitleLoadResult(source, projectedCues.Count, source == SubtitlePipelineSource.Sidecar, false);
    }

    private async Task<SubtitleLoadResult> StartAutomaticCaptionGenerationAsync(
        string videoPath,
        CancellationToken cancellationToken,
        bool preserveCurrentTranslationPreference = false)
    {
        var operationId = $"captions-{Guid.NewGuid():N}";
        CancelCaptionGeneration();
        CancelTranslationWork();
        if (!preserveCurrentTranslationPreference)
        {
            InitializeTranslationPreferencesForNewVideo();
        }

        var transcriptionModel = SubtitleWorkflowCatalog.GetTranscriptionModel(_workflowStateStore.Snapshot.SelectedTranscriptionModelKey);
        var generationId = _workflowStateStore.Snapshot.ActiveCaptionGenerationId + 1;
        UpdateWorkflowState(state => state with
        {
            CurrentVideoPath = videoPath,
            CurrentSourceLanguage = DefaultSourceLanguage,
            ActiveCaptionGenerationId = generationId,
            ActiveCaptionGenerationModelKey = transcriptionModel.Key,
            CaptionGenerationModeLabel = transcriptionModel.DisplayName,
            OverlayStatus = _workflowStateStore.Snapshot.IsTranslationEnabled
                ? "Listening to the video audio and building translated captions..."
                : "Listening to the video audio and building subtitles..."
        });
        _mediaSessionCoordinator.ClearTranscriptSegments(SubtitlePipelineSource.Generated, true, null, DefaultSourceLanguage);
        _mediaSessionCoordinator.SetCaptionGenerationState(true);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captionGenerationCts = cts;
        _logger.LogInfo("Automatic caption generation starting.", BabelLogContext.Create(("operationId", operationId), ("videoPath", videoPath), ("modelKey", transcriptionModel.Key), ("preserveTranslationPreference", preserveCurrentTranslationPreference)));
        PublishStatus($"Generating captions with {transcriptionModel.DisplayName}.", _workflowStateStore.Snapshot.OverlayStatus);

        try
        {
            var generatedCues = await _captionGenerator.GenerateCaptionsAsync(
                videoPath,
                transcriptionModel,
                null,
                chunk => HandleRecognizedChunk(chunk, generationId),
                progress => HandleSubtitleModelTransferProgress(progress, generationId),
                cts.Token);

            if (generationId != _workflowStateStore.Snapshot.ActiveCaptionGenerationId || cts.IsCancellationRequested)
            {
                return new SubtitleLoadResult(SubtitlePipelineSource.Generated, CurrentCues.Count, false, true);
            }

            if (CurrentCues.Count == 0 && generatedCues.Count > 0)
            {
                var clonedCues = CloneCues(generatedCues);
                var currentSourceLanguage = ApplySourceLanguageToCues(clonedCues);
                UpdateWorkflowState(state => state with
                {
                    CurrentSourceLanguage = currentSourceLanguage
                });
                _mediaSessionCoordinator.SetTranscriptSegments(
                    BuildTranscriptSegments(clonedCues, SubtitlePipelineSource.Generated, videoPath, transcriptionModel.Key),
                    SubtitlePipelineSource.Generated,
                    currentSourceLanguage);
                ApplyAutomaticTranslationPreferenceIfNeeded();
            }

            _mediaSessionCoordinator.SetCaptionGenerationState(false);
            CacheGeneratedSubtitles(videoPath, transcriptionModel.Key, CurrentCues);
            UpdateWorkflowState(state => state with
            {
                OverlayStatus = CurrentCues.Count > 0 ? null : "No speech could be recognized from the video audio."
            });
            _logger.LogInfo("Automatic caption generation completed.", BabelLogContext.Create(("operationId", operationId), ("videoPath", videoPath), ("cueCount", CurrentCues.Count), ("modelKey", transcriptionModel.Key)));
            PublishStatus(
                CurrentCues.Count > 0
                    ? $"Generated {CurrentCues.Count} caption cues automatically."
                    : "No speech could be recognized from the video audio.",
                _workflowStateStore.Snapshot.OverlayStatus);
        }
        catch (OperationCanceledException)
        {
            _mediaSessionCoordinator.SetCaptionGenerationState(false);
            _logger.LogInfo("Automatic caption generation canceled.", BabelLogContext.Create(("operationId", operationId), ("videoPath", videoPath), ("modelKey", transcriptionModel.Key)));
        }
        catch (Exception ex)
        {
            if (generationId == _workflowStateStore.Snapshot.ActiveCaptionGenerationId)
            {
                _mediaSessionCoordinator.SetCaptionGenerationState(false);
                UpdateWorkflowState(state => state with
                {
                    OverlayStatus = "Automatic caption generation failed. You can still load a manual subtitle file."
                });
                _logger.LogError("Automatic caption generation failed.", ex, BabelLogContext.Create(("operationId", operationId), ("videoPath", videoPath), ("modelKey", transcriptionModel.Key)));
                PublishStatus(
                    $"Automatic caption generation failed: {ex.Message}",
                    _workflowStateStore.Snapshot.OverlayStatus);
            }
        }

        return new SubtitleLoadResult(SubtitlePipelineSource.Generated, CurrentCues.Count, false, true);
    }
}
