using System.Diagnostics;
using BabelPlayer.Core;

namespace BabelPlayer.App;

internal interface ICaptionGenerationHost
{
    void ApplyAutomaticTranslationPreferenceIfNeeded();
    void CancelTranslationWork();
    void InitializeTranslationPreferencesForNewVideo();
    void PublishStatus(string message, string? overlayStatus);
    Task TranslateCueAsync(TranscriptSegment cue, CancellationToken cancellationToken);
}

internal sealed class CaptionGenerationOrchestrator
{
    private const string DefaultSourceLanguage = "und";

    private readonly ICaptionGenerator _captionGenerator;
    private readonly MediaSessionCoordinator _mediaSessionCoordinator;
    private readonly ISubtitleWorkflowStateStore _workflowStateStore;
    private readonly GeneratedSubtitleCache _generatedSubtitleCache = new();
    private readonly ICaptionGenerationHost _host;
    private readonly IBabelLogger _logger;

    private CancellationTokenSource? _captionGenerationCts;

    internal CaptionGenerationOrchestrator(
        ICaptionGenerator captionGenerator,
        MediaSessionCoordinator mediaSessionCoordinator,
        ISubtitleWorkflowStateStore workflowStateStore,
        ICaptionGenerationHost host,
        IBabelLogger logger)
    {
        _captionGenerator = captionGenerator;
        _mediaSessionCoordinator = mediaSessionCoordinator;
        _workflowStateStore = workflowStateStore;
        _host = host;
        _logger = logger;
    }

    private IReadOnlyList<SubtitleCue> CurrentCues => MediaSessionProjection.ToSubtitleCues(_mediaSessionCoordinator.Snapshot);

    internal async Task<SubtitleLoadResult> StartAutomaticCaptionGenerationAsync(
        string videoPath,
        CancellationToken cancellationToken,
        bool preserveCurrentTranslationPreference = false)
    {
        var operationId = $"captions-{Guid.NewGuid():N}";
        using var activity = BabelTracing.Source.StartActivity("subtitle.generate_captions");
        activity?.SetTag(BabelTracing.Tags.MediaPath, videoPath);
        CancelCaptionGeneration();
        _host.CancelTranslationWork();
        if (!preserveCurrentTranslationPreference)
        {
            _host.InitializeTranslationPreferencesForNewVideo();
        }

        var transcriptionModel = SubtitleWorkflowCatalog.GetTranscriptionModel(_workflowStateStore.Snapshot.SelectedTranscriptionModelKey);
        var generationId = _workflowStateStore.Snapshot.ActiveCaptionGenerationId + 1;
        _workflowStateStore.Update(state => state with
        {
            CurrentVideoPath = videoPath,
            ActiveCaptionGenerationId = generationId,
            ActiveCaptionGenerationModelKey = transcriptionModel.Key,
            CaptionGenerationModeLabel = transcriptionModel.DisplayName,
            OverlayStatus = _mediaSessionCoordinator.Snapshot.Translation.IsEnabled
                ? "Listening to the video audio and building translated captions..."
                : "Listening to the video audio and building subtitles..."
        });
        _mediaSessionCoordinator.ClearTranscriptSegments(SubtitlePipelineSource.Generated, true, null, DefaultSourceLanguage);
        _mediaSessionCoordinator.SetCaptionGenerationState(true);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captionGenerationCts = cts;
        _logger.LogInfo("Automatic caption generation starting.", BabelLogContext.Create(("operationId", operationId), ("videoPath", videoPath), ("modelKey", transcriptionModel.Key), ("preserveTranslationPreference", preserveCurrentTranslationPreference)));
        activity?.SetTag(BabelTracing.Tags.ModelKey, transcriptionModel.Key);
        _host.PublishStatus($"Generating captions with {transcriptionModel.DisplayName}.", _workflowStateStore.Snapshot.OverlayStatus);

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
                var clonedCues = SubtitleCueSessionMapper.CloneCues(generatedCues);
                var currentSourceLanguage = SubtitleCueSessionMapper.ApplySourceLanguageToCues(clonedCues, DefaultSourceLanguage);
                _mediaSessionCoordinator.SetTranscriptSegments(
                    SubtitleCueSessionMapper.BuildTranscriptSegments(
                        clonedCues,
                        SubtitlePipelineSource.Generated,
                        videoPath,
                        transcriptionModel.Key,
                        transcriptionModel.DisplayName,
                        DefaultSourceLanguage),
                    SubtitlePipelineSource.Generated,
                    currentSourceLanguage);
                _host.ApplyAutomaticTranslationPreferenceIfNeeded();
            }

            _mediaSessionCoordinator.SetCaptionGenerationState(false);
            CacheGeneratedSubtitles(videoPath, transcriptionModel.Key, CurrentCues);
            _workflowStateStore.Update(state => state with
            {
                OverlayStatus = CurrentCues.Count > 0 ? null : "No speech could be recognized from the video audio."
            });
            _logger.LogInfo("Automatic caption generation completed.", BabelLogContext.Create(("operationId", operationId), ("videoPath", videoPath), ("cueCount", CurrentCues.Count), ("modelKey", transcriptionModel.Key)));
            activity?.SetTag(BabelTracing.Tags.CueCount, CurrentCues.Count);
            _host.PublishStatus(
                CurrentCues.Count > 0
                    ? $"Generated {CurrentCues.Count} caption cues automatically."
                    : "No speech could be recognized from the video audio.",
                _workflowStateStore.Snapshot.OverlayStatus);
        }
        catch (OperationCanceledException)
        {
            _mediaSessionCoordinator.SetCaptionGenerationState(false);
            _logger.LogInfo("Automatic caption generation canceled.", BabelLogContext.Create(("operationId", operationId), ("videoPath", videoPath), ("modelKey", transcriptionModel.Key)));
            activity?.SetStatus(ActivityStatusCode.Error, "Canceled");
        }
        catch (Exception ex)
        {
            if (generationId == _workflowStateStore.Snapshot.ActiveCaptionGenerationId)
            {
                _mediaSessionCoordinator.SetCaptionGenerationState(false);
                _workflowStateStore.Update(state => state with
                {
                    OverlayStatus = "Automatic caption generation failed. You can still load a manual subtitle file."
                });
                _logger.LogError("Automatic caption generation failed.", ex, BabelLogContext.Create(("operationId", operationId), ("videoPath", videoPath), ("modelKey", transcriptionModel.Key)));
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag(BabelTracing.Tags.ErrorMessage, ex.Message);
                _host.PublishStatus(
                    $"Automatic caption generation failed: {ex.Message}",
                    _workflowStateStore.Snapshot.OverlayStatus);
            }
        }

        return new SubtitleLoadResult(SubtitlePipelineSource.Generated, CurrentCues.Count, false, true);
    }

    internal void CancelCaptionGeneration()
    {
        _captionGenerationCts?.Cancel();
        _captionGenerationCts?.Dispose();
        _captionGenerationCts = null;
        _mediaSessionCoordinator.SetCaptionGenerationState(false);
        _workflowStateStore.Update(state => state with
        {
            ActiveCaptionGenerationModelKey = null
        });
    }

    internal bool TryLoadCachedGeneratedSubtitles(string videoPath, string transcriptionModelKey)
    {
        if (!_generatedSubtitleCache.TryGet(videoPath, transcriptionModelKey, out var cachedCues))
        {
            return false;
        }

        var currentSourceLanguage = SubtitleCueSessionMapper.ApplySourceLanguageToCues(cachedCues, DefaultSourceLanguage);
        _workflowStateStore.Update(state => state with
        {
            CurrentVideoPath = videoPath,
            OverlayStatus = null
        });
        _mediaSessionCoordinator.SetTranscriptSegments(
            SubtitleCueSessionMapper.BuildTranscriptSegments(
                cachedCues,
                SubtitlePipelineSource.Generated,
                videoPath,
                transcriptionModelKey,
                SubtitleWorkflowCatalog.GetTranscriptionModel(transcriptionModelKey).DisplayName,
                DefaultSourceLanguage),
            SubtitlePipelineSource.Generated,
            currentSourceLanguage);
        _mediaSessionCoordinator.SetCaptionGenerationState(false);
        return true;
    }

    private void CacheGeneratedSubtitles(string videoPath, string transcriptionModelKey, IReadOnlyList<SubtitleCue> cues)
    {
        _generatedSubtitleCache.Store(videoPath, transcriptionModelKey, cues);
    }

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

        _workflowStateStore.Update(state => state with
        {
            OverlayStatus = null
        });
        _mediaSessionCoordinator.SetCaptionGenerationState(true);
        _mediaSessionCoordinator.UpsertTranscriptSegment(transcriptSegment, currentSourceLanguage);
        _host.ApplyAutomaticTranslationPreferenceIfNeeded();

        if (!string.IsNullOrWhiteSpace(_workflowStateStore.Snapshot.CurrentVideoPath))
        {
            CacheGeneratedSubtitles(_workflowStateStore.Snapshot.CurrentVideoPath!, activeModelKey, CurrentCues);
        }

        _host.PublishStatus(
            $"Generating captions ({_workflowStateStore.Snapshot.CaptionGenerationModeLabel})... captions ready through {SubtitleCueSessionMapper.FormatClock(cue.End)}.",
            null);
        _ = _host.TranslateCueAsync(transcriptSegment, _captionGenerationCts?.Token ?? CancellationToken.None);
    }

    private void HandleSubtitleModelTransferProgress(ModelTransferProgress progress, int generationId)
    {
        if (generationId != _workflowStateStore.Snapshot.ActiveCaptionGenerationId)
        {
            return;
        }

        var message = SubtitleCueSessionMapper.FormatSubtitleModelTransferStatus(progress);
        _host.PublishStatus(message, message);
    }
}
