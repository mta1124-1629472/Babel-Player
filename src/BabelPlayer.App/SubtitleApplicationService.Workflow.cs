using BabelPlayer.Core;

namespace BabelPlayer.App;
using System.Diagnostics;

public sealed partial class SubtitleApplicationService
{
    private void LoadPersistedSelections()
    {
        var transcriptionKey = SubtitleWorkflowCatalog.CanonicalizeTranscriptionModelKey(
            _providerAvailabilityService.ResolvePersistedTranscriptionModelKey(_credentialFacade.GetSubtitleModelKey()));
        var translationKey = _providerAvailabilityService.ResolvePersistedTranslationModelKey(_credentialFacade.GetTranslationModelKey());
        var autoTranslateEnabled = _credentialFacade.GetAutoTranslateEnabled();

        UpdateWorkflowState(state => state with
        {
            SelectedTranscriptionModelKey = transcriptionKey,
            SelectedTranslationModelKey = translationKey,
            CaptionGenerationModeLabel = SubtitleWorkflowCatalog.GetTranscriptionModel(transcriptionKey).DisplayName
        });
        _mediaSessionCoordinator.SetTranslationState(_mediaSessionCoordinator.Snapshot.Translation.IsEnabled, autoTranslateEnabled);
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
        var session = _mediaSessionCoordinator.Snapshot;
        if (state.CurrentVideoTranslationPreferenceLocked)
        {
            return;
        }

        if (!session.Translation.AutoTranslateEnabled || string.IsNullOrWhiteSpace(state.SelectedTranslationModelKey))
        {
            SetTranslationEnabledForCurrentVideo(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(session.LanguageAnalysis.CurrentSourceLanguage)
            || string.Equals(session.LanguageAnalysis.CurrentSourceLanguage, DefaultSourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetTranslationEnabledForCurrentVideo(!SubtitleCueSessionMapper.IsLanguageCode(session.LanguageAnalysis.CurrentSourceLanguage, _autoTranslatePreferredSourceLanguage));
    }

    private void SetTranslationEnabledForCurrentVideo(bool enabled)
    {
        _mediaSessionCoordinator.SetTranslationState(enabled, _mediaSessionCoordinator.Snapshot.Translation.AutoTranslateEnabled);
    }

    private async Task ReprocessCurrentSubtitlesForTranslationSettingsAsync(CancellationToken cancellationToken)
    {
        var state = _workflowStateStore.Snapshot;
        var isTranslationEnabled = _mediaSessionCoordinator.Snapshot.Translation.IsEnabled;
        _logger.LogInfo("Reprocessing subtitles for translation settings.", BabelLogContext.Create(("translationEnabled", isTranslationEnabled), ("modelKey", state.SelectedTranslationModelKey)));
        if (isTranslationEnabled && string.IsNullOrWhiteSpace(state.SelectedTranslationModelKey))
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
        PublishStatus(isTranslationEnabled
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

        if (_captionOrchestrator.TryLoadCachedGeneratedSubtitles(currentVideoPath, selection.Key))
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

        await _captionOrchestrator.StartAutomaticCaptionGenerationAsync(currentVideoPath, cancellationToken, preserveCurrentTranslationPreference: true);
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
        _captionOrchestrator.CancelCaptionGeneration();
        CancelTranslationWork();
        if (!preserveCurrentTranslationPreference)
        {
            InitializeTranslationPreferencesForNewVideo();
        }

        UpdateWorkflowState(state => state with
        {
            OverlayStatus = null
        });

        var projectedCues = SubtitleCueSessionMapper.CloneCues(cues);
        lock (_translationSync)
        {
            _inFlightCueTranslations.Clear();
        }

        if (projectedCues.Count == 0)
        {
            UpdateWorkflowState(state => state with
            {
                OverlayStatus = "Loaded subtitle file contains no playable cues."
            });
            _mediaSessionCoordinator.ClearTranscriptSegments(source, false, "Loaded subtitle file contains no playable cues.", DefaultSourceLanguage);
            PublishStatus("No playable subtitle cues were found.", "Loaded subtitle file contains no playable cues.");
            return new SubtitleLoadResult(source, 0, source == SubtitlePipelineSource.Sidecar, false);
        }

        var currentSourceLanguage = SubtitleCueSessionMapper.ApplySourceLanguageToCues(projectedCues, DefaultSourceLanguage);
        _mediaSessionCoordinator.SetTranscriptSegments(
            SubtitleCueSessionMapper.BuildTranscriptSegments(
                projectedCues,
                source,
                currentVideoPath,
                null,
                SubtitleWorkflowCatalog.GetTranscriptionModel(_workflowStateStore.Snapshot.SelectedTranscriptionModelKey).DisplayName,
                DefaultSourceLanguage),
            source,
            currentSourceLanguage);
        ApplyAutomaticTranslationPreferenceIfNeeded();

        PublishStatus(
            $"{statusPrefix} ({projectedCues.Count} cues).",
            _mediaSessionCoordinator.Snapshot.Translation.IsEnabled
                ? "Preparing translated subtitles..."
                : "Preparing source-language subtitles...");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = cts;
        _ = TranslateAllCuesAsync(cts.Token);

        return new SubtitleLoadResult(source, projectedCues.Count, source == SubtitlePipelineSource.Sidecar, false);
    }
}
