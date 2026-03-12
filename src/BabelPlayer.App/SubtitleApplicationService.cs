using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed partial class SubtitleApplicationService : IDisposable
{
    private const string DefaultSourceLanguage = "und";
    private const string DefaultTargetLanguage = "en";

    private readonly ISubtitleSourceResolver _sourceResolver;
    private readonly ICaptionGenerator _captionGenerator;
    private readonly ISubtitleTranslator _subtitleTranslator;
    private readonly IAiCredentialCoordinator _aiCredentialCoordinator;
    private readonly IRuntimeProvisioner _runtimeProvisioner;
    private readonly IProviderAvailabilityService _providerAvailabilityService;
    private readonly CredentialFacade _credentialFacade;
    private readonly MediaSessionCoordinator _mediaSessionCoordinator;
    private readonly ISubtitleWorkflowStateStore _workflowStateStore;
    private readonly IBabelLogger _logger;
    private readonly object _translationSync = new();
    private readonly HashSet<string> _inFlightCueTranslations = [];
    private readonly Dictionary<string, List<SubtitleCue>> _generatedSubtitleCache = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _translationCts;
    private CancellationTokenSource? _captionGenerationCts;
    private readonly string _translationTargetLanguage = DefaultTargetLanguage;
    private readonly string _autoTranslatePreferredSourceLanguage = DefaultTargetLanguage;
    private string? _lastObservedActiveTranscriptSegmentId;
    private bool _disposed;

    public SubtitleApplicationService(
        ISubtitleSourceResolver sourceResolver,
        ICaptionGenerator captionGenerator,
        ISubtitleTranslator subtitleTranslator,
        IAiCredentialCoordinator aiCredentialCoordinator,
        IRuntimeProvisioner runtimeProvisioner,
        CredentialFacade credentialFacade,
        MediaSessionCoordinator mediaSessionCoordinator,
        ISubtitleWorkflowStateStore workflowStateStore,
        IProviderAvailabilityService providerAvailabilityService,
        IBabelLogFactory? logFactory = null)
    {
        _sourceResolver = sourceResolver;
        _captionGenerator = captionGenerator;
        _subtitleTranslator = subtitleTranslator;
        _aiCredentialCoordinator = aiCredentialCoordinator;
        _runtimeProvisioner = runtimeProvisioner;
        _credentialFacade = credentialFacade;
        _mediaSessionCoordinator = mediaSessionCoordinator;
        _workflowStateStore = workflowStateStore;
        _providerAvailabilityService = providerAvailabilityService;
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("subtitles.workflow");
        _subtitleTranslator.RuntimeStatusChanged += HandleLocalTranslationRuntimeStatus;
        _mediaSessionCoordinator.Store.SnapshotChanged += HandleMediaSessionSnapshotChanged;
        _lastObservedActiveTranscriptSegmentId = _mediaSessionCoordinator.Snapshot.SubtitlePresentation.ActiveTranscriptSegmentId;
    }

    public event Action<string>? StatusChanged;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgressChanged;

    public IMediaSessionStore MediaSessionStore => _mediaSessionCoordinator.Store;

    public ISubtitleWorkflowStateStore WorkflowStateStore => _workflowStateStore;

    public IReadOnlyList<SubtitleCue> CurrentCues => MediaSessionProjection.ToSubtitleCues(_mediaSessionCoordinator.Snapshot);

    public bool HasCurrentCues => _mediaSessionCoordinator.Snapshot.Transcript.Segments.Count > 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _mediaSessionCoordinator.Store.SnapshotChanged -= HandleMediaSessionSnapshotChanged;
        _subtitleTranslator.RuntimeStatusChanged -= HandleLocalTranslationRuntimeStatus;
        CancelTranslationWork();
        CancelCaptionGeneration();
        _disposed = true;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadPersistedSelections();
        _logger.LogInfo("Subtitle application service initialized.", BabelLogContext.Create(("transcriptionModel", _workflowStateStore.Snapshot.SelectedTranscriptionModelKey), ("translationModel", _workflowStateStore.Snapshot.SelectedTranslationModelKey)));
        return Task.CompletedTask;
    }

    public async Task<bool> SelectTranscriptionModelAsync(
        string modelKey,
        CancellationToken cancellationToken = default,
        bool suppressStatus = false)
    {
        var selection = SubtitleWorkflowCatalog.GetTranscriptionModel(modelKey);
        _logger.LogInfo("Selecting transcription model.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider)));
        if (selection.Provider == TranscriptionProvider.Cloud && !await _aiCredentialCoordinator.EnsureOpenAiApiKeyAsync(cancellationToken))
        {
            if (!suppressStatus)
            {
                PublishStatus("Cloud transcription model selection canceled.");
            }

            return false;
        }

        _credentialFacade.SaveSubtitleModelKey(selection.Key);
        UpdateWorkflowState(state => state with
        {
            SelectedTranscriptionModelKey = selection.Key,
            CaptionGenerationModeLabel = selection.DisplayName
        });

        await ReprocessCurrentSubtitlesForTranscriptionModelAsync(selection, cancellationToken, suppressStatus);
        return true;
    }

    public async Task<bool> SelectTranslationModelAsync(string modelKey, CancellationToken cancellationToken = default)
    {
        var selection = SubtitleWorkflowCatalog.GetTranslationModel(modelKey);
        _logger.LogInfo("Selecting translation model.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider)));
        if (selection.Provider == TranslationProvider.None || !await EnsureTranslationProviderReadyAsync(selection, cancellationToken))
        {
            PublishStatus("Translation model selection canceled.");
            return false;
        }

        var previousModelKey = _workflowStateStore.Snapshot.SelectedTranslationModelKey;
        _credentialFacade.SaveTranslationModelKey(selection.Key);
        UpdateWorkflowState(state => state with
        {
            SelectedTranslationModelKey = selection.Key
        });

        PublishStatus($"Selected translation model: {selection.DisplayName}.");
        if (_workflowStateStore.Snapshot.IsTranslationEnabled)
        {
            await ReprocessCurrentSubtitlesForTranslationSettingsAsync(cancellationToken);
        }

        return true;
    }

    public async Task SetTranslationEnabledAsync(bool enabled, bool lockPreference = true, CancellationToken cancellationToken = default)
    {
        UpdateWorkflowState(state => state with
        {
            CurrentVideoTranslationPreferenceLocked = lockPreference
        });
        SetTranslationEnabledForCurrentVideo(enabled);

        if (!enabled)
        {
            await ReprocessCurrentSubtitlesForTranslationSettingsAsync(cancellationToken);
            return;
        }

        var translationModelKey = _workflowStateStore.Snapshot.SelectedTranslationModelKey;
        if (string.IsNullOrWhiteSpace(translationModelKey))
        {
            PublishStatus("Select a translation model to start translating this video.");
            return;
        }

        var selection = SubtitleWorkflowCatalog.GetTranslationModel(translationModelKey);
        if (!await EnsureTranslationProviderReadyAsync(selection, cancellationToken))
        {
            SetTranslationEnabledForCurrentVideo(false);
            PublishStatus("Translation activation canceled.");
            return;
        }

        await ReprocessCurrentSubtitlesForTranslationSettingsAsync(cancellationToken);
    }

    public async Task SetAutoTranslateEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (enabled && string.IsNullOrWhiteSpace(_workflowStateStore.Snapshot.SelectedTranslationModelKey))
        {
            PublishStatus("Select a translation model before enabling auto-translate.");
            return;
        }

        _credentialFacade.SaveAutoTranslateEnabled(enabled);
        UpdateWorkflowState(state => state with
        {
            AutoTranslateEnabled = enabled,
            CurrentVideoTranslationPreferenceLocked = false
        });
        ApplyAutomaticTranslationPreferenceIfNeeded();
        await ReprocessCurrentSubtitlesForTranslationSettingsAsync(cancellationToken);
    }

    public async Task<SubtitleLoadResult> LoadMediaSubtitlesAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        _logger.LogInfo("Loading media subtitles.", BabelLogContext.Create(("videoPath", videoPath)));

        CancelCaptionGeneration();
        CancelTranslationWork();
        UpdateWorkflowState(state => state with
        {
            CurrentVideoPath = videoPath,
            CurrentSourceLanguage = DefaultSourceLanguage,
            OverlayStatus = "No sidecar subtitles found. Generating captions from the video audio."
        });
        _mediaSessionCoordinator.OpenMedia(videoPath);
        _mediaSessionCoordinator.ClearTranscriptSegments(SubtitlePipelineSource.None, false, null, DefaultSourceLanguage);
        InitializeTranslationPreferencesForNewVideo();

        var sidecarPath = Path.ChangeExtension(videoPath, ".srt");
        if (File.Exists(sidecarPath))
        {
            return await ImportExternalSubtitlesAsync(sidecarPath, autoLoaded: true, cancellationToken);
        }

        if (TryLoadCachedGeneratedSubtitles(videoPath, _workflowStateStore.Snapshot.SelectedTranscriptionModelKey))
        {
            return await LoadSubtitleCuesAsync(
                CurrentCues,
                SubtitlePipelineSource.Generated,
                $"Loaded cached generated captions ({SubtitleWorkflowCatalog.GetTranscriptionModel(_workflowStateStore.Snapshot.SelectedTranscriptionModelKey).DisplayName})",
                cancellationToken);
        }

        return await StartAutomaticCaptionGenerationAsync(videoPath, cancellationToken);
    }

    public async Task<SubtitleLoadResult> ImportExternalSubtitlesAsync(string path, bool autoLoaded = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _logger.LogInfo("Importing external subtitles.", BabelLogContext.Create(("path", path), ("autoLoaded", autoLoaded)));

        try
        {
            var cues = await _sourceResolver.LoadExternalSubtitleCuesAsync(
                path,
                HandleFfmpegRuntimeInstallProgress,
                message => PublishStatus(message, message),
                cancellationToken);

            return await LoadSubtitleCuesAsync(
                cues,
                autoLoaded ? SubtitlePipelineSource.Sidecar : SubtitlePipelineSource.Manual,
                autoLoaded
                    ? $"Loaded sidecar subtitles: {Path.GetFileName(path)}"
                    : $"Loaded subtitles: {Path.GetFileName(path)}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("External subtitle import failed.", ex, BabelLogContext.Create(("path", path), ("autoLoaded", autoLoaded)));
            PublishStatus(ex.Message, "Subtitle import failed.");
            return new SubtitleLoadResult(SubtitlePipelineSource.None, 0, false, false);
        }
    }

    public async Task<SubtitleLoadResult> ImportEmbeddedSubtitleTrackAsync(string videoPath, MediaTrackInfo track, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentNullException.ThrowIfNull(track);
        _logger.LogInfo("Importing embedded subtitle track.", BabelLogContext.Create(("videoPath", videoPath), ("trackId", track.Id), ("codec", track.Codec)));

        CancelCaptionGeneration();
        UpdateWorkflowState(state => state with
        {
            CurrentVideoPath = videoPath
        });

        try
        {
            var cues = await _sourceResolver.ExtractEmbeddedSubtitleCuesAsync(
                videoPath,
                track,
                HandleFfmpegRuntimeInstallProgress,
                message => PublishStatus(message, message),
                cancellationToken);

            return await LoadSubtitleCuesAsync(
                cues,
                SubtitlePipelineSource.EmbeddedTrack,
                $"Imported embedded subtitle track {track.Id}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Embedded subtitle import failed.", ex, BabelLogContext.Create(("videoPath", videoPath), ("trackId", track.Id), ("codec", track.Codec)));
            PublishStatus(ex.Message, "Embedded subtitle import failed.");
            return new SubtitleLoadResult(SubtitlePipelineSource.None, 0, false, false);
        }
    }

    public Task<IReadOnlyList<SubtitleCue>> LoadExternalSubtitleCuesAsync(
        string path,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
        => _sourceResolver.LoadExternalSubtitleCuesAsync(path, onRuntimeProgress, onStatus, cancellationToken);

    public Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(
        string videoPath,
        MediaTrackInfo track,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
        => _sourceResolver.ExtractEmbeddedSubtitleCuesAsync(videoPath, track, onRuntimeProgress, onStatus, cancellationToken);

    public Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
        string videoPath,
        TranscriptionModelSelection selection,
        string? languageHint,
        Action<TranscriptChunk>? onFinal,
        Action<ModelTransferProgress>? onProgress,
        CancellationToken cancellationToken)
        => _captionGenerator.GenerateCaptionsAsync(videoPath, selection, languageHint, onFinal, onProgress, cancellationToken);

    public Task<string> TranslateAsync(
        TranslationModelSelection selection,
        string text,
        CancellationToken cancellationToken)
        => _subtitleTranslator.TranslateAsync(selection, text, cancellationToken);

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationModelSelection selection,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
        => _subtitleTranslator.TranslateBatchAsync(selection, texts, cancellationToken);

    public Task<bool> EnsureOpenAiApiKeyAsync(CancellationToken cancellationToken)
        => _aiCredentialCoordinator.EnsureOpenAiApiKeyAsync(cancellationToken);

    public Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider, CancellationToken cancellationToken)
        => _aiCredentialCoordinator.EnsureTranslationProviderCredentialsAsync(provider, cancellationToken);

    public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => _runtimeProvisioner.EnsureLlamaCppAsync(onProgress, cancellationToken);

    public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => _runtimeProvisioner.EnsureFfmpegAsync(onProgress, cancellationToken);

    private void HandleMediaSessionSnapshotChanged(MediaSessionSnapshot snapshot)
    {
        var activeTranscriptId = snapshot.SubtitlePresentation.ActiveTranscriptSegmentId;
        if (string.Equals(_lastObservedActiveTranscriptSegmentId, activeTranscriptId, StringComparison.Ordinal))
        {
            return;
        }

        _lastObservedActiveTranscriptSegmentId = activeTranscriptId;
        if (!_workflowStateStore.Snapshot.IsTranslationEnabled || string.IsNullOrWhiteSpace(activeTranscriptId))
        {
            return;
        }

        var activeTranscript = GetActiveTranscriptSegment(snapshot);
        if (activeTranscript is null || HasTranslatedSegment(activeTranscript, snapshot))
        {
            return;
        }

        _ = TranslateCueAsync(activeTranscript, _translationCts?.Token ?? CancellationToken.None);
    }
}
