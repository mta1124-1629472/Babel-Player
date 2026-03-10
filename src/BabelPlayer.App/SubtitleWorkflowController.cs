using System.Diagnostics;
using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class SubtitleWorkflowController
{
    private const string DefaultSourceLanguage = "und";
    private const string DefaultTargetLanguage = "en";

    private readonly SubtitleManager _subtitleManager = new();
    private readonly MtService _translator = new();
    private readonly CredentialFacade _credentialFacade;
    private readonly ICredentialDialogService? _credentialDialogService;
    private readonly IFilePickerService? _filePickerService;
    private readonly IRuntimeBootstrapService _runtimeBootstrapService;
    private readonly Func<string, string?> _environmentVariableReader;
    private readonly Func<string, CancellationToken, Task> _validateOpenAiApiKeyAsync;
    private readonly Func<CloudTranslationOptions, CancellationToken, Task> _validateTranslationProviderAsync;
    private readonly Func<string, CaptionGenerationOptions, Action<TranscriptChunk>, Action<ModelTransferProgress>, CancellationToken, Task<IReadOnlyList<SubtitleCue>>> _transcribeVideoAsync;
    private readonly object _translationSync = new();
    private readonly HashSet<SubtitleCue> _inFlightCueTranslations = [];
    private readonly Dictionary<string, List<SubtitleCue>> _generatedSubtitleCache = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _translationCts;
    private CancellationTokenSource? _captionGenerationCts;
    private string? _sessionOpenAiApiKey;
    private string? _currentVideoPath;
    private string _selectedTranscriptionModelKey = SubtitleWorkflowCatalog.DefaultTranscriptionModelKey;
    private string? _selectedTranslationModelKey;
    private bool _isTranslationEnabledForCurrentVideo;
    private bool _autoTranslateVideosOutsidePreferredLanguage;
    private bool _currentVideoTranslationPreferenceLocked;
    private string _translationTargetLanguage = DefaultTargetLanguage;
    private string _autoTranslatePreferredSourceLanguage = DefaultTargetLanguage;
    private string _currentSourceLanguage = DefaultSourceLanguage;
    private SubtitlePipelineSource _subtitleSource = SubtitlePipelineSource.None;
    private bool _isCaptionGenerationInProgress;
    private string? _overlayStatusText;
    private string _captionGenerationModeLabel = SubtitleWorkflowCatalog.GetTranscriptionModel(SubtitleWorkflowCatalog.DefaultTranscriptionModelKey).DisplayName;
    private int _activeCaptionGenerationId;
    private string? _activeCaptionGenerationModelKey;
    private SubtitleCue? _activeCue;

    public SubtitleWorkflowController()
        : this(
            new CredentialFacade(),
            null,
            null,
            new RuntimeBootstrapService(),
            Environment.GetEnvironmentVariable,
            MtService.ValidateApiKeyAsync,
            MtService.ValidateTranslationProviderAsync)
    {
    }

    public SubtitleWorkflowController(
        CredentialFacade credentialFacade,
        ICredentialDialogService? credentialDialogService = null,
        IFilePickerService? filePickerService = null,
        IRuntimeBootstrapService? runtimeBootstrapService = null,
        Func<string, string?>? environmentVariableReader = null,
        Func<string, CancellationToken, Task>? validateOpenAiApiKeyAsync = null,
        Func<CloudTranslationOptions, CancellationToken, Task>? validateTranslationProviderAsync = null,
        Func<string, CaptionGenerationOptions, Action<TranscriptChunk>, Action<ModelTransferProgress>, CancellationToken, Task<IReadOnlyList<SubtitleCue>>>? transcribeVideoAsync = null)
    {
        _credentialFacade = credentialFacade;
        _credentialDialogService = credentialDialogService;
        _filePickerService = filePickerService;
        _runtimeBootstrapService = runtimeBootstrapService ?? new RuntimeBootstrapService();
        _environmentVariableReader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        _validateOpenAiApiKeyAsync = validateOpenAiApiKeyAsync ?? MtService.ValidateApiKeyAsync;
        _validateTranslationProviderAsync = validateTranslationProviderAsync ?? MtService.ValidateTranslationProviderAsync;
        _transcribeVideoAsync = transcribeVideoAsync ?? DefaultTranscribeVideoAsync;
        _autoTranslateVideosOutsidePreferredLanguage = _credentialFacade.GetAutoTranslateEnabled();
        _sessionOpenAiApiKey = _environmentVariableReader("OPENAI_API_KEY") ?? _credentialFacade.GetOpenAiApiKey();
        _translator.OnLocalRuntimeStatus += HandleLocalTranslationRuntimeStatus;

        LoadPersistedSelections();
    }

    public event Action<SubtitleWorkflowSnapshot>? SnapshotChanged;
    public event Action<string>? StatusChanged;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgressChanged;

    public SubtitleWorkflowSnapshot Snapshot => BuildSnapshot();

    public IReadOnlyList<SubtitleCue> CurrentCues => _subtitleManager.Cues;

    public bool HasCurrentCues => _subtitleManager.HasCues;

    public SubtitleOverlayPresentation GetOverlayPresentation(SubtitleRenderMode renderMode, bool subtitlesVisible = true)
    {
        return BuildOverlayPresentation(renderMode, subtitlesVisible, _activeCue, _overlayStatusText);
    }

    public void ExportCurrentSubtitles(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _subtitleManager.ExportSrt(path);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadPersistedSelections();
        PublishSnapshot();
        await Task.CompletedTask;
    }

    public SubtitleRenderMode ToggleSource(SubtitleRenderMode current)
    {
        return current switch
        {
            SubtitleRenderMode.Off => SubtitleRenderMode.SourceOnly,
            SubtitleRenderMode.SourceOnly => SubtitleRenderMode.Off,
            SubtitleRenderMode.TranslationOnly => SubtitleRenderMode.Dual,
            SubtitleRenderMode.Dual => SubtitleRenderMode.TranslationOnly,
            _ => SubtitleRenderMode.TranslationOnly
        };
    }

    public SubtitleRenderMode ToggleTranslation(SubtitleRenderMode current)
    {
        return current switch
        {
            SubtitleRenderMode.Off => SubtitleRenderMode.TranslationOnly,
            SubtitleRenderMode.SourceOnly => SubtitleRenderMode.Dual,
            SubtitleRenderMode.TranslationOnly => SubtitleRenderMode.Off,
            SubtitleRenderMode.Dual => SubtitleRenderMode.SourceOnly,
            _ => SubtitleRenderMode.TranslationOnly
        };
    }

    public SubtitleStyleSettings UpdateStyle(
        SubtitleStyleSettings current,
        double? sourceFontSize = null,
        double? translationFontSize = null,
        double? backgroundOpacity = null,
        double? bottomMargin = null,
        double? dualSpacing = null,
        string? sourceForegroundHex = null,
        string? translationForegroundHex = null)
    {
        return current with
        {
            SourceFontSize = sourceFontSize ?? current.SourceFontSize,
            TranslationFontSize = translationFontSize ?? current.TranslationFontSize,
            BackgroundOpacity = backgroundOpacity ?? current.BackgroundOpacity,
            BottomMargin = bottomMargin ?? current.BottomMargin,
            DualSpacing = dualSpacing ?? current.DualSpacing,
            SourceForegroundHex = sourceForegroundHex ?? current.SourceForegroundHex,
            TranslationForegroundHex = translationForegroundHex ?? current.TranslationForegroundHex
        };
    }

    public async Task<bool> SelectTranscriptionModelAsync(string modelKey, CancellationToken cancellationToken = default)
    {
        var selection = SubtitleWorkflowCatalog.GetTranscriptionModel(modelKey);
        if (selection.Provider == TranscriptionProvider.Cloud && !await EnsureOpenAiApiKeyAsync(cancellationToken))
        {
            PublishStatus("Cloud transcription model selection canceled.");
            return false;
        }

        _selectedTranscriptionModelKey = selection.Key;
        _credentialFacade.SaveSubtitleModelKey(selection.Key);
        PublishSnapshot();
        await ReprocessCurrentSubtitlesForTranscriptionModelAsync(selection, cancellationToken);
        return true;
    }

    public async Task<bool> SelectTranslationModelAsync(string modelKey, CancellationToken cancellationToken = default)
    {
        var selection = SubtitleWorkflowCatalog.GetTranslationModel(modelKey);
        if (selection.Provider == TranslationProvider.None || !await EnsureTranslationProviderCredentialsAsync(selection.Provider, cancellationToken))
        {
            PublishStatus("Translation model selection canceled.");
            return false;
        }

        var previousModelKey = _selectedTranslationModelKey;
        _selectedTranslationModelKey = selection.Key;
        _credentialFacade.SaveTranslationModelKey(selection.Key);
        ConfigureTranslator();

        if (selection.Provider is TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B)
        {
            var warmedUp = await WarmupSelectedLocalTranslationRuntimeAsync(selection, cancellationToken);
            if (!warmedUp)
            {
                RestoreTranslationSelection(previousModelKey);
                return false;
            }
        }

        PublishStatus($"Selected translation model: {selection.DisplayName}.");
        PublishSnapshot();
        if (_isTranslationEnabledForCurrentVideo)
        {
            await ReprocessCurrentSubtitlesForTranslationSettingsAsync(cancellationToken);
        }

        return true;
    }

    public async Task SetTranslationEnabledAsync(bool enabled, bool lockPreference = true, CancellationToken cancellationToken = default)
    {
        _currentVideoTranslationPreferenceLocked = lockPreference;
        SetTranslationEnabledForCurrentVideo(enabled);
        PublishSnapshot();

        if (!enabled)
        {
            await ReprocessCurrentSubtitlesForTranslationSettingsAsync(cancellationToken);
            return;
        }

        if (!HasSelectedTranslationModel())
        {
            PublishStatus("Select a translation model to start translating this video.");
            return;
        }

        var selection = SubtitleWorkflowCatalog.GetTranslationModel(_selectedTranslationModelKey);
        if (!await EnsureTranslationProviderCredentialsAsync(selection.Provider, cancellationToken))
        {
            SetTranslationEnabledForCurrentVideo(false);
            PublishSnapshot();
            PublishStatus("Translation activation canceled.");
            return;
        }

        if (selection.Provider is TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B)
        {
            var warmedUp = await WarmupSelectedLocalTranslationRuntimeAsync(selection, cancellationToken);
            if (!warmedUp)
            {
                SetTranslationEnabledForCurrentVideo(false);
                PublishSnapshot();
                return;
            }
        }

        await ReprocessCurrentSubtitlesForTranslationSettingsAsync(cancellationToken);
    }

    public async Task SetAutoTranslateEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (enabled && !HasSelectedTranslationModel())
        {
            PublishStatus("Select a translation model before enabling auto-translate.");
            return;
        }

        _autoTranslateVideosOutsidePreferredLanguage = enabled;
        _credentialFacade.SaveAutoTranslateEnabled(enabled);
        _currentVideoTranslationPreferenceLocked = false;
        ApplyAutomaticTranslationPreferenceIfNeeded();
        PublishSnapshot();
        await ReprocessCurrentSubtitlesForTranslationSettingsAsync(cancellationToken);
    }

    public async Task<SubtitleLoadResult> LoadMediaSubtitlesAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        _currentVideoPath = videoPath;
        var sidecarPath = Path.ChangeExtension(videoPath, ".srt");
        if (File.Exists(sidecarPath))
        {
            return await ImportExternalSubtitlesAsync(sidecarPath, autoLoaded: true, cancellationToken);
        }

        CancelTranslationWork();
        _subtitleManager.Clear();
        _subtitleSource = SubtitlePipelineSource.None;
        _activeCue = null;
        _currentSourceLanguage = DefaultSourceLanguage;
        InitializeTranslationPreferencesForNewVideo();
        SetOverlayStatus("No sidecar subtitles found. Generating captions from the video audio.");
        PublishSnapshot();

        if (TryLoadCachedGeneratedSubtitles(videoPath, _selectedTranscriptionModelKey))
        {
            return await LoadSubtitleCuesAsync(
                CloneCues(_subtitleManager.Cues),
                SubtitlePipelineSource.Generated,
                $"Loaded cached generated captions ({SubtitleWorkflowCatalog.GetTranscriptionModel(_selectedTranscriptionModelKey).DisplayName})",
                cancellationToken);
        }

        return await StartAutomaticCaptionGenerationAsync(videoPath, cancellationToken);
    }

    public async Task<SubtitleLoadResult> ImportExternalSubtitlesAsync(string path, bool autoLoaded = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        IReadOnlyList<SubtitleCue> cues;
        try
        {
            cues = await SubtitleImportService.LoadExternalSubtitleCuesAsync(
                path,
                HandleFfmpegRuntimeInstallProgress,
                message => PublishStatus(message, message),
                cancellationToken);
        }
        catch (Exception ex)
        {
            PublishStatus(ex.Message, "Subtitle import failed.");
            return new SubtitleLoadResult(SubtitlePipelineSource.None, 0, false, false);
        }

        return await LoadSubtitleCuesAsync(
            cues,
            autoLoaded ? SubtitlePipelineSource.Sidecar : SubtitlePipelineSource.Manual,
            autoLoaded
                ? $"Loaded sidecar subtitles: {Path.GetFileName(path)}"
                : $"Loaded subtitles: {Path.GetFileName(path)}",
            cancellationToken);
    }

    public async Task<SubtitleLoadResult> ImportEmbeddedSubtitleTrackAsync(string videoPath, MediaTrackInfo track, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentNullException.ThrowIfNull(track);

        _currentVideoPath = videoPath;
        CancelCaptionGeneration();

        try
        {
            var cues = await SubtitleImportService.ExtractEmbeddedSubtitleCuesAsync(
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
            PublishStatus(ex.Message, "Embedded subtitle import failed.");
            return new SubtitleLoadResult(SubtitlePipelineSource.None, 0, false, false);
        }
    }

    public Task<IReadOnlyList<SubtitleCue>> LoadExternalSubtitleCuesAsync(
        string path,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        return CaptureLoadAsync(
            progress => onRuntimeProgress?.Invoke(progress),
            message => onStatus?.Invoke(message),
            async token =>
            {
                await ImportExternalSubtitlesAsync(path, autoLoaded: false, token);
                return Snapshot.Cues;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(
        string videoPath,
        MediaTrackInfo track,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        return CaptureLoadAsync(
            progress => onRuntimeProgress?.Invoke(progress),
            message => onStatus?.Invoke(message),
            async token =>
            {
                await ImportEmbeddedSubtitleTrackAsync(videoPath, track, token);
                return Snapshot.Cues;
            },
            cancellationToken);
    }

    public void UpdatePlaybackPosition(TimeSpan position)
    {
        if (!_subtitleManager.HasCues)
        {
            _activeCue = null;
            PublishSnapshot();
            return;
        }

        _activeCue = _subtitleManager.GetCueAt(position);
        if (_activeCue is not null && string.IsNullOrWhiteSpace(_activeCue.TranslatedText))
        {
            _ = TranslateCueAsync(_activeCue, _translationCts?.Token ?? CancellationToken.None);
        }

        PublishSnapshot();
    }

    private async Task<T> CaptureLoadAsync<T>(
        Action<RuntimeInstallProgress> runtimeHandler,
        Action<string> statusHandler,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        RuntimeInstallProgressChanged += runtimeHandler;
        StatusChanged += statusHandler;
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            RuntimeInstallProgressChanged -= runtimeHandler;
            StatusChanged -= statusHandler;
        }
    }

    private static async Task<IReadOnlyList<SubtitleCue>> DefaultTranscribeVideoAsync(
        string videoPath,
        CaptionGenerationOptions options,
        Action<TranscriptChunk> onFinal,
        Action<ModelTransferProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var asrService = new AsrService();
        asrService.OnFinal += onFinal;
        asrService.OnModelTransferProgress += onProgress;
        return await asrService.TranscribeVideoAsync(videoPath, options, cancellationToken);
    }

    private bool TryLoadCachedGeneratedSubtitles(string videoPath, string transcriptionModelKey)
    {
        if (!_generatedSubtitleCache.TryGetValue(GetGeneratedSubtitleCacheKey(videoPath, transcriptionModelKey), out var cachedCues))
        {
            return false;
        }

        _subtitleManager.LoadCues(CloneCues(cachedCues));
        _subtitleSource = SubtitlePipelineSource.Generated;
        _activeCue = null;
        _currentSourceLanguage = ApplySourceLanguageToCues(_subtitleManager.Cues);
        SetOverlayStatus(null);
        PublishSnapshot();
        return true;
    }

    private void CacheGeneratedSubtitles(string videoPath, string transcriptionModelKey, IReadOnlyList<SubtitleCue> cues)
    {
        _generatedSubtitleCache[GetGeneratedSubtitleCacheKey(videoPath, transcriptionModelKey)] = CloneCues(cues).ToList();
    }

    private static string GetGeneratedSubtitleCacheKey(string videoPath, string transcriptionModelKey)
    {
        return $"{Path.GetFullPath(videoPath)}|{transcriptionModelKey}";
    }

    private static IReadOnlyList<SubtitleCue> CloneCues(IReadOnlyList<SubtitleCue> cues)
    {
        return cues.Select(cue => new SubtitleCue
        {
            Start = cue.Start,
            End = cue.End,
            SourceText = cue.SourceText,
            SourceLanguage = cue.SourceLanguage,
            TranslatedText = cue.TranslatedText
        }).ToList();
    }

    private void LoadPersistedSelections()
    {
        _selectedTranscriptionModelKey = ResolvePersistedTranscriptionModelKey(_credentialFacade.GetSubtitleModelKey());
        _selectedTranslationModelKey = ResolvePersistedTranslationModelKey(_credentialFacade.GetTranslationModelKey());
        _autoTranslateVideosOutsidePreferredLanguage = _credentialFacade.GetAutoTranslateEnabled();
        _sessionOpenAiApiKey = _environmentVariableReader("OPENAI_API_KEY") ?? _credentialFacade.GetOpenAiApiKey();
        ConfigureTranslator();
    }

    private string ResolvePersistedTranscriptionModelKey(string? modelKey)
    {
        var selection = SubtitleWorkflowCatalog.GetTranscriptionModel(modelKey);
        return selection.Provider == TranscriptionProvider.Cloud && !HasOpenAiApiKey()
            ? SubtitleWorkflowCatalog.DefaultTranscriptionModelKey
            : selection.Key;
    }

    private string? ResolvePersistedTranslationModelKey(string? modelKey)
    {
        var selection = SubtitleWorkflowCatalog.GetTranslationModel(modelKey);
        if (selection.Provider == TranslationProvider.None)
        {
            return null;
        }

        if (selection.Provider is TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B)
        {
            return TryResolveLlamaCppServerPath() is not null ? selection.Key : null;
        }

        return HasConfiguredTranslationProvider(selection.Provider) ? selection.Key : null;
    }

    private SubtitleWorkflowSnapshot BuildSnapshot()
    {
        return new SubtitleWorkflowSnapshot
        {
            CurrentVideoPath = _currentVideoPath,
            SelectedTranscriptionModelKey = _selectedTranscriptionModelKey,
            SelectedTranscriptionLabel = SubtitleWorkflowCatalog.GetTranscriptionModel(_selectedTranscriptionModelKey).DisplayName,
            SelectedTranslationModelKey = _selectedTranslationModelKey,
            SelectedTranslationLabel = SubtitleWorkflowCatalog.GetTranslationModel(_selectedTranslationModelKey).DisplayName,
            IsTranslationEnabled = _isTranslationEnabledForCurrentVideo,
            AutoTranslateEnabled = _autoTranslateVideosOutsidePreferredLanguage,
            IsCaptionGenerationInProgress = _isCaptionGenerationInProgress,
            CurrentSourceLanguage = _currentSourceLanguage,
            SubtitleSource = _subtitleSource,
            OverlayStatus = _overlayStatusText,
            ActiveCue = _activeCue,
            Cues = _subtitleManager.Cues
        };
    }

    internal static SubtitleOverlayPresentation BuildOverlayPresentation(
        SubtitleRenderMode renderMode,
        bool subtitlesVisible,
        SubtitleCue? cue,
        string? overlayStatus)
    {
        if (!subtitlesVisible || renderMode == SubtitleRenderMode.Off)
        {
            return new SubtitleOverlayPresentation();
        }

        var sourceText = cue?.SourceText?.Trim();
        var translatedText = cue?.DisplayText?.Trim();
        if (string.IsNullOrWhiteSpace(translatedText))
        {
            translatedText = overlayStatus;
        }

        if (string.IsNullOrWhiteSpace(sourceText) && string.IsNullOrWhiteSpace(translatedText))
        {
            return new SubtitleOverlayPresentation();
        }

        var showSecondaryLine = renderMode == SubtitleRenderMode.Dual
            && !string.IsNullOrWhiteSpace(sourceText)
            && !string.Equals(sourceText, translatedText, StringComparison.Ordinal);

        var primaryText = renderMode switch
        {
            SubtitleRenderMode.SourceOnly => sourceText,
            SubtitleRenderMode.TranslationOnly => translatedText,
            SubtitleRenderMode.Dual when !string.IsNullOrWhiteSpace(translatedText) => translatedText,
            SubtitleRenderMode.Dual => sourceText,
            _ => translatedText
        };

        if (string.IsNullOrWhiteSpace(primaryText))
        {
            primaryText = sourceText;
        }

        return new SubtitleOverlayPresentation
        {
            IsVisible = !string.IsNullOrWhiteSpace(primaryText) || showSecondaryLine,
            PrimaryText = primaryText ?? string.Empty,
            SecondaryText = showSecondaryLine ? sourceText ?? string.Empty : string.Empty
        };
    }

    private async Task<SubtitleLoadResult> LoadSubtitleCuesAsync(
        IReadOnlyList<SubtitleCue> cues,
        SubtitlePipelineSource source,
        string statusPrefix,
        CancellationToken cancellationToken,
        bool preserveCurrentTranslationPreference = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!preserveCurrentTranslationPreference)
        {
            InitializeTranslationPreferencesForNewVideo();
        }
        CancelTranslationWork();
        _subtitleManager.LoadCues(cues);
        _subtitleSource = source;
        _activeCue = null;
        _currentSourceLanguage = DefaultSourceLanguage;

        lock (_translationSync)
        {
            _inFlightCueTranslations.Clear();
        }

        if (!_subtitleManager.HasCues)
        {
            PublishStatus("No playable subtitle cues were found.", "Loaded subtitle file contains no playable cues.");
            return new SubtitleLoadResult(source, 0, source == SubtitlePipelineSource.Sidecar, false);
        }

        _currentSourceLanguage = ApplySourceLanguageToCues(_subtitleManager.Cues);
        ApplyAutomaticTranslationPreferenceIfNeeded();

        PublishStatus(
            $"{statusPrefix} ({_subtitleManager.CueCount} cues).",
            _isTranslationEnabledForCurrentVideo
                ? "Preparing translated subtitles..."
                : "Preparing source-language subtitles...");

        var cts = new CancellationTokenSource();
        _translationCts = cts;
        _ = TranslateAllCuesAsync(cts.Token);
        PublishSnapshot();

        return new SubtitleLoadResult(source, _subtitleManager.CueCount, source == SubtitlePipelineSource.Sidecar, false);
    }

    private async Task<SubtitleLoadResult> StartAutomaticCaptionGenerationAsync(string videoPath, CancellationToken cancellationToken, bool preserveCurrentTranslationPreference = false)
    {
        CancelCaptionGeneration();
        CancelTranslationWork();
        _subtitleManager.Clear();
        _activeCue = null;
        _currentSourceLanguage = DefaultSourceLanguage;
        if (!preserveCurrentTranslationPreference)
        {
            InitializeTranslationPreferencesForNewVideo();
        }
        _subtitleSource = SubtitlePipelineSource.Generated;
        _isCaptionGenerationInProgress = true;

        var generationId = Interlocked.Increment(ref _activeCaptionGenerationId);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captionGenerationCts = cts;

        var transcriptionModel = SubtitleWorkflowCatalog.GetTranscriptionModel(_selectedTranscriptionModelKey);
        _activeCaptionGenerationModelKey = transcriptionModel.Key;
        var apiKey = _sessionOpenAiApiKey;
        var mode = transcriptionModel.Provider == TranscriptionProvider.Cloud && !string.IsNullOrWhiteSpace(apiKey)
            ? CaptionTranscriptionMode.Cloud
            : CaptionTranscriptionMode.Local;
        _captionGenerationModeLabel = transcriptionModel.DisplayName;

        if (transcriptionModel.Provider == TranscriptionProvider.Cloud && string.IsNullOrWhiteSpace(apiKey))
        {
            _selectedTranscriptionModelKey = SubtitleWorkflowCatalog.DefaultTranscriptionModelKey;
            transcriptionModel = SubtitleWorkflowCatalog.GetTranscriptionModel(_selectedTranscriptionModelKey);
            _activeCaptionGenerationModelKey = transcriptionModel.Key;
            mode = CaptionTranscriptionMode.Local;
            _captionGenerationModeLabel = transcriptionModel.DisplayName;
            PublishStatus("OpenAI API key is missing. Reverting to local transcription.", "OpenAI API key is missing. Reverting to local transcription.");
        }
        else
        {
            PublishStatus(
                $"Generating captions with {transcriptionModel.DisplayName}.",
                _isTranslationEnabledForCurrentVideo
                    ? "Listening to the video audio and building translated captions..."
                    : "Listening to the video audio and building subtitles...");
        }

        PublishSnapshot();

        try
        {
            var generatedCues = await _transcribeVideoAsync(
                videoPath,
                new CaptionGenerationOptions
                {
                    Mode = mode,
                    LanguageHint = null,
                    OpenAiApiKey = apiKey,
                    LocalModelType = transcriptionModel.LocalModelType,
                    CloudModel = transcriptionModel.CloudModel
                },
                chunk => HandleRecognizedChunk(chunk, generationId),
                progress => HandleSubtitleModelTransferProgress(progress, generationId),
                cts.Token);

            if (generationId != _activeCaptionGenerationId || cts.IsCancellationRequested)
            {
                return new SubtitleLoadResult(SubtitlePipelineSource.Generated, _subtitleManager.CueCount, false, true);
            }

            if (_subtitleManager.CueCount == 0 && generatedCues.Count > 0)
            {
                _subtitleManager.LoadCues(CloneCues(generatedCues));
                _currentSourceLanguage = ApplySourceLanguageToCues(_subtitleManager.Cues);
                ApplyAutomaticTranslationPreferenceIfNeeded();
            }

            _isCaptionGenerationInProgress = false;
            CacheGeneratedSubtitles(videoPath, _activeCaptionGenerationModelKey ?? transcriptionModel.Key, _subtitleManager.Cues);
            PublishStatus(
                _subtitleManager.CueCount > 0
                    ? $"Generated {_subtitleManager.CueCount} caption cues automatically."
                    : "No speech could be recognized from the video audio.",
                _subtitleManager.CueCount > 0
                    ? null
                    : "No speech could be recognized from the video audio.");
        }
        catch (OperationCanceledException)
        {
            _isCaptionGenerationInProgress = false;
        }
        catch (Exception ex)
        {
            if (generationId == _activeCaptionGenerationId)
            {
                _isCaptionGenerationInProgress = false;
                PublishStatus(
                    $"Automatic caption generation failed: {ex.Message}",
                    "Automatic caption generation failed. You can still load a manual subtitle file.");
            }
        }

        PublishSnapshot();
        return new SubtitleLoadResult(SubtitlePipelineSource.Generated, _subtitleManager.CueCount, false, true);
    }

    private void HandleRecognizedChunk(TranscriptChunk chunk, int generationId)
    {
        if (generationId != _activeCaptionGenerationId || string.IsNullOrWhiteSpace(chunk.Text))
        {
            return;
        }

        var cue = new SubtitleCue
        {
            Start = TimeSpan.FromSeconds(chunk.StartTimeSec),
            End = TimeSpan.FromSeconds(chunk.EndTimeSec),
            SourceText = chunk.Text.Trim(),
            SourceLanguage = ResolveSourceLanguage(chunk.Text)
        };

        _currentSourceLanguage = ResolveAggregateSourceLanguage(_currentSourceLanguage, cue.SourceLanguage);
        ApplyAutomaticTranslationPreferenceIfNeeded();

        lock (_translationSync)
        {
            _subtitleManager.AddCue(cue);
        }

        if (!string.IsNullOrWhiteSpace(_currentVideoPath))
        {
            CacheGeneratedSubtitles(_currentVideoPath, _activeCaptionGenerationModelKey ?? _selectedTranscriptionModelKey, _subtitleManager.Cues);
        }

        PublishStatus(
            $"Generating captions ({_captionGenerationModeLabel})... captions ready through {FormatClock(cue.End)}.",
            null);
        SetOverlayStatus(null);
        _ = TranslateCueAsync(cue, _captionGenerationCts?.Token ?? CancellationToken.None);
        PublishSnapshot();
    }

    private async Task TranslateAllCuesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_isTranslationEnabledForCurrentVideo && _translator.UseCloudTranslation)
            {
                var cues = _subtitleManager.Cues.ToList();
                var translatedTexts = await TranslateCueBatchAsync(cues, cancellationToken);
                for (var index = 0; index < cues.Count; index++)
                {
                    lock (_translationSync)
                    {
                        _subtitleManager.CommitTranslation(cues[index], translatedTexts[index]);
                    }
                }
            }
            else
            {
                foreach (var cue in _subtitleManager.Cues.ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await TranslateCueAsync(cue, cancellationToken);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                PublishStatus(_isTranslationEnabledForCurrentVideo
                    ? $"Prepared {_subtitleManager.CueCount} translated subtitle cues."
                    : $"Prepared {_subtitleManager.CueCount} source-language subtitle cues.");
            }
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
            PublishSnapshot();
        }
    }

    private async Task TranslateCueAsync(SubtitleCue cue, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(cue.TranslatedText))
        {
            return;
        }

        cue.SourceLanguage ??= ResolveSourceLanguage(cue.SourceText);
        if (ShouldUseTranscriptDirectly(cue))
        {
            lock (_translationSync)
            {
                _subtitleManager.CommitTranslation(cue, cue.SourceText.Trim());
            }

            PublishSnapshot();
            return;
        }

        lock (_translationSync)
        {
            if (!string.IsNullOrWhiteSpace(cue.TranslatedText) || !_inFlightCueTranslations.Add(cue))
            {
                return;
            }
        }

        try
        {
            PublishLocalTranslationPreparationStatus();
            var translated = await _translator.TranslateAsync(cue.SourceText, cancellationToken);

            lock (_translationSync)
            {
                _subtitleManager.CommitTranslation(cue, translated);
            }

            PublishSnapshot();
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
                _inFlightCueTranslations.Remove(cue);
            }
        }
    }

    private bool ShouldUseTranscriptDirectly(SubtitleCue cue)
    {
        if (!_isTranslationEnabledForCurrentVideo || !HasSelectedTranslationModel())
        {
            return true;
        }

        return IsLanguageCode(cue.SourceLanguage ?? _currentSourceLanguage, _translationTargetLanguage);
    }

    private async Task<IReadOnlyList<string>> TranslateCueBatchAsync(IReadOnlyList<SubtitleCue> cues, CancellationToken cancellationToken)
    {
        const int batchSize = 20;
        var translated = new List<string>(cues.Count);
        for (var index = 0; index < cues.Count; index += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = cues.Skip(index).Take(batchSize).ToList();
            var batchTranslations = await _translator.TranslateBatchAsync(batch.Select(cue => cue.SourceText).ToList(), cancellationToken);
            translated.AddRange(batchTranslations);
        }

        return translated;
    }

    private async Task HandleCloudServiceFailureAsync(Exception ex)
    {
        if (ShouldDisableCloudForError(ex))
        {
            if (SubtitleWorkflowCatalog.IsCloudTranslationProvider(SubtitleWorkflowCatalog.GetTranslationModel(_selectedTranslationModelKey).Provider))
            {
                RestoreTranslationSelection(null);
            }

            if (SubtitleWorkflowCatalog.GetTranscriptionModel(_selectedTranscriptionModelKey).Provider == TranscriptionProvider.Cloud)
            {
                _selectedTranscriptionModelKey = SubtitleWorkflowCatalog.DefaultTranscriptionModelKey;
            }

            ConfigureTranslator();
            PublishStatus("Cloud models were disabled after a quota or rate-limit error.");
            PublishSnapshot();
            return;
        }

        PublishStatus(ex.Message);
        await Task.CompletedTask;
    }

    private static bool ShouldDisableCloudForError(Exception ex)
    {
        return ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> WarmupSelectedLocalTranslationRuntimeAsync(TranslationModelSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            PublishStatus($"Preparing {selection.DisplayName}.", $"Preparing {selection.DisplayName}.");
            await _translator.WarmupLocalRuntimeAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            PublishStatus(ex.Message, "Local translation model setup failed.");
            return false;
        }
    }

    private async Task<bool> EnsureOpenAiApiKeyAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_sessionOpenAiApiKey))
        {
            return true;
        }

        if (_credentialDialogService is null)
        {
            return false;
        }

        var apiKey = await _credentialDialogService.PromptForApiKeyAsync(
            "OpenAI API Key",
            "Enter an OpenAI API key. This is used for cloud transcription and OpenAI translation.",
            "Use Key",
            cancellationToken);
        return !string.IsNullOrWhiteSpace(apiKey) && await SaveOpenAiApiKeyAsync(apiKey, cancellationToken);
    }

    private async Task<bool> SaveOpenAiApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            PublishStatus("Validating OpenAI API key...", "Validating OpenAI API key...");
            await _validateOpenAiApiKeyAsync(apiKey, cancellationToken);
            _credentialFacade.SaveOpenAiApiKey(apiKey);
            _sessionOpenAiApiKey = apiKey.Trim();
            ConfigureTranslator();
            PublishStatus("OpenAI API key saved.");
            PublishSnapshot();
            return true;
        }
        catch (Exception ex)
        {
            PublishStatus(ex.Message, "OpenAI API key validation failed.");
            return false;
        }
    }

    private async Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider, CancellationToken cancellationToken)
    {
        switch (provider)
        {
            case TranslationProvider.LocalHyMt15_1_8B:
            case TranslationProvider.LocalHyMt15_7B:
                return TryResolveLlamaCppServerPath() is not null || await EnsureLlamaCppRuntimeAsync(cancellationToken);
            case TranslationProvider.OpenAi:
                return await EnsureOpenAiApiKeyAsync(cancellationToken);
            case TranslationProvider.Google:
                return GetGoogleCloudTranslationOptions() is not null || await PromptAndSaveGoogleTranslateCredentialsAsync(cancellationToken);
            case TranslationProvider.DeepL:
                return GetDeepLCloudTranslationOptions() is not null || await PromptAndSaveDeepLCredentialsAsync(cancellationToken);
            case TranslationProvider.MicrosoftTranslator:
                return GetMicrosoftCloudTranslationOptions() is not null || await PromptAndSaveMicrosoftTranslatorCredentialsAsync(cancellationToken);
            default:
                return false;
        }
    }

    private async Task<bool> PromptAndSaveGoogleTranslateCredentialsAsync(CancellationToken cancellationToken)
    {
        if (_credentialDialogService is null)
        {
            return false;
        }

        var apiKey = await _credentialDialogService.PromptForApiKeyAsync(
            "Google Translate API Key",
            "Enter the Google Translate API key. It will be validated and saved for future sessions.",
            "Save Key",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        return await SaveCloudTranslationCredentialsAsync(
            new CloudTranslationOptions(CloudTranslationProvider.Google, apiKey.Trim()),
            "Google Translate",
            options => _credentialFacade.SaveGoogleTranslateApiKey(options.ApiKey),
            cancellationToken);
    }

    private async Task<bool> PromptAndSaveDeepLCredentialsAsync(CancellationToken cancellationToken)
    {
        if (_credentialDialogService is null)
        {
            return false;
        }

        var apiKey = await _credentialDialogService.PromptForApiKeyAsync(
            "DeepL API Key",
            "Enter the DeepL API key. It will be validated and saved for future sessions.",
            "Save Key",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        return await SaveCloudTranslationCredentialsAsync(
            new CloudTranslationOptions(CloudTranslationProvider.DeepL, apiKey.Trim()),
            "DeepL",
            options => _credentialFacade.SaveDeepLApiKey(options.ApiKey),
            cancellationToken);
    }

    private async Task<bool> PromptAndSaveMicrosoftTranslatorCredentialsAsync(CancellationToken cancellationToken)
    {
        if (_credentialDialogService is null)
        {
            return false;
        }

        var credentials = await _credentialDialogService.PromptForApiKeyWithRegionAsync(
            "Microsoft Translator Credentials",
            "Enter the Microsoft Translator API key and Azure region. Both values are required.",
            "Save Credentials",
            cancellationToken);
        if (credentials is null)
        {
            return false;
        }

        return await SaveCloudTranslationCredentialsAsync(
            new CloudTranslationOptions(
                CloudTranslationProvider.MicrosoftTranslator,
                credentials.Value.ApiKey.Trim(),
                null,
                credentials.Value.Region.Trim()),
            "Microsoft Translator",
            options =>
            {
                _credentialFacade.SaveMicrosoftTranslatorApiKey(options.ApiKey);
                if (!string.IsNullOrWhiteSpace(options.Region))
                {
                    _credentialFacade.SaveMicrosoftTranslatorRegion(options.Region);
                }
            },
            cancellationToken);
    }

    private async Task<bool> SaveCloudTranslationCredentialsAsync(
        CloudTranslationOptions options,
        string providerLabel,
        Action<CloudTranslationOptions> persist,
        CancellationToken cancellationToken)
    {
        try
        {
            PublishStatus($"Validating {providerLabel} credentials...", $"Validating {providerLabel} credentials...");
            await _validateTranslationProviderAsync(options, cancellationToken);
            persist(options);
            ConfigureTranslator();
            PublishStatus($"{providerLabel} credentials saved.");
            PublishSnapshot();
            return true;
        }
        catch (Exception ex)
        {
            PublishStatus(ex.Message, $"{providerLabel} credentials are invalid.");
            return false;
        }
    }

    private async Task<bool> EnsureLlamaCppRuntimeAsync(CancellationToken cancellationToken)
    {
        if (_credentialDialogService is null)
        {
            return false;
        }

        var choice = await _credentialDialogService.PromptForLlamaCppBootstrapChoiceAsync(
            "llama.cpp Setup",
            "Local HY-MT translation needs llama-server. Install it automatically or choose an existing executable.",
            cancellationToken);

        switch (choice)
        {
            case LlamaCppBootstrapChoice.InstallAutomatically:
                return await InstallLlamaCppRuntimeAsync(cancellationToken);
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

                _credentialFacade.SaveLlamaCppServerPath(selectedPath);
                _credentialFacade.SaveLlamaCppRuntimeSource("manual");
                ConfigureTranslator();
                PublishStatus($"Using llama.cpp runtime: {Path.GetFileName(selectedPath)}");
                PublishSnapshot();
                return true;
            }
            case LlamaCppBootstrapChoice.OpenOfficialDownloadPage:
                OpenExternalLink(LlamaCppRuntimeInstaller.ReleasePageUrl);
                PublishStatus("Opened the official llama.cpp release page.");
                return false;
            default:
                return false;
        }
    }

    private async Task<bool> InstallLlamaCppRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var serverPath = await _runtimeBootstrapService.EnsureLlamaCppAsync(HandleLlamaRuntimeInstallProgress, cancellationToken);
            _credentialFacade.SaveLlamaCppServerPath(serverPath);
            _credentialFacade.SaveLlamaCppRuntimeVersion(LlamaCppRuntimeInstaller.RuntimeVersion);
            _credentialFacade.SaveLlamaCppRuntimeSource(LlamaCppRuntimeInstaller.RuntimeSource);
            ConfigureTranslator();
            PublishStatus("llama.cpp runtime is ready.", "llama.cpp runtime is ready.");
            PublishSnapshot();
            return true;
        }
        catch (Exception ex)
        {
            PublishStatus($"llama.cpp runtime install failed: {ex.Message}", "llama.cpp runtime install failed.");
            return false;
        }
    }

    private void ConfigureTranslator()
    {
        if (!HasSelectedTranslationModel())
        {
            _translator.ConfigureLocal(new LocalTranslationOptions(OfflineTranslationModel.None));
            _translator.ConfigureCloud(null);
            return;
        }

        var selection = SubtitleWorkflowCatalog.GetTranslationModel(_selectedTranslationModelKey);
        _translator.ConfigureLocal(GetLocalTranslationOptions(selection));
        _translator.ConfigureCloud(GetCloudTranslationOptions(selection));
    }

    private LocalTranslationOptions GetLocalTranslationOptions(TranslationModelSelection selection)
    {
        var llamaServerPath = _environmentVariableReader("LLAMA_SERVER_PATH")
            ?? _credentialFacade.GetLlamaCppServerPath();

        return selection.Provider switch
        {
            TranslationProvider.LocalHyMt15_1_8B => new LocalTranslationOptions(OfflineTranslationModel.HyMt15_1_8B, llamaServerPath),
            TranslationProvider.LocalHyMt15_7B => new LocalTranslationOptions(OfflineTranslationModel.HyMt15_7B, llamaServerPath),
            _ => new LocalTranslationOptions(OfflineTranslationModel.None)
        };
    }

    private CloudTranslationOptions? GetCloudTranslationOptions(TranslationModelSelection selection)
    {
        return selection.Provider switch
        {
            TranslationProvider.OpenAi when !string.IsNullOrWhiteSpace(_sessionOpenAiApiKey)
                => new CloudTranslationOptions(CloudTranslationProvider.OpenAi, _sessionOpenAiApiKey!, selection.CloudModel),
            TranslationProvider.Google => GetGoogleCloudTranslationOptions(),
            TranslationProvider.DeepL => GetDeepLCloudTranslationOptions(),
            TranslationProvider.MicrosoftTranslator => GetMicrosoftCloudTranslationOptions(),
            _ => null
        };
    }

    private CloudTranslationOptions? GetGoogleCloudTranslationOptions()
    {
        var apiKey = _environmentVariableReader("GOOGLE_TRANSLATE_API_KEY")
            ?? _environmentVariableReader("GOOGLE_CLOUD_TRANSLATE_API_KEY")
            ?? _credentialFacade.GetGoogleTranslateApiKey();
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new CloudTranslationOptions(CloudTranslationProvider.Google, apiKey.Trim());
    }

    private CloudTranslationOptions? GetDeepLCloudTranslationOptions()
    {
        var apiKey = _environmentVariableReader("DEEPL_API_KEY")
            ?? _credentialFacade.GetDeepLApiKey();
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new CloudTranslationOptions(CloudTranslationProvider.DeepL, apiKey.Trim());
    }

    private CloudTranslationOptions? GetMicrosoftCloudTranslationOptions()
    {
        var apiKey = _environmentVariableReader("MICROSOFT_TRANSLATOR_API_KEY")
            ?? _environmentVariableReader("AZURE_TRANSLATOR_KEY")
            ?? _credentialFacade.GetMicrosoftTranslatorApiKey();
        var region = _environmentVariableReader("MICROSOFT_TRANSLATOR_REGION")
            ?? _environmentVariableReader("AZURE_TRANSLATOR_REGION")
            ?? _credentialFacade.GetMicrosoftTranslatorRegion();

        return string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(region)
            ? null
            : new CloudTranslationOptions(CloudTranslationProvider.MicrosoftTranslator, apiKey.Trim(), null, region.Trim());
    }

    private bool HasOpenAiApiKey()
    {
        return !string.IsNullOrWhiteSpace(_sessionOpenAiApiKey)
            || !string.IsNullOrWhiteSpace(_environmentVariableReader("OPENAI_API_KEY"))
            || !string.IsNullOrWhiteSpace(_credentialFacade.GetOpenAiApiKey());
    }

    private bool HasConfiguredTranslationProvider(TranslationProvider provider)
    {
        return provider switch
        {
            TranslationProvider.None => false,
            TranslationProvider.LocalHyMt15_1_8B => TryResolveLlamaCppServerPath() is not null,
            TranslationProvider.LocalHyMt15_7B => TryResolveLlamaCppServerPath() is not null,
            TranslationProvider.OpenAi => HasOpenAiApiKey(),
            TranslationProvider.Google => GetGoogleCloudTranslationOptions() is not null,
            TranslationProvider.DeepL => GetDeepLCloudTranslationOptions() is not null,
            TranslationProvider.MicrosoftTranslator => GetMicrosoftCloudTranslationOptions() is not null,
            _ => false
        };
    }

    private string? TryResolveLlamaCppServerPath()
    {
        var configuredPath = _environmentVariableReader("LLAMA_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        configuredPath = _credentialFacade.GetLlamaCppServerPath();
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var installedPath = LlamaCppRuntimeInstaller.GetInstalledServerPath();
        if (File.Exists(installedPath))
        {
            return installedPath;
        }

        var pathValue = _environmentVariableReader("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exePath = Path.Combine(segment, "llama-server.exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }

            var noExtensionPath = Path.Combine(segment, "llama-server");
            if (File.Exists(noExtensionPath))
            {
                return noExtensionPath;
            }
        }

        return null;
    }

    private void InitializeTranslationPreferencesForNewVideo()
    {
        _currentVideoTranslationPreferenceLocked = false;
        SetTranslationEnabledForCurrentVideo(false);
    }

    private void ApplyAutomaticTranslationPreferenceIfNeeded()
    {
        if (_currentVideoTranslationPreferenceLocked)
        {
            return;
        }

        if (!_autoTranslateVideosOutsidePreferredLanguage || !HasSelectedTranslationModel())
        {
            SetTranslationEnabledForCurrentVideo(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentSourceLanguage) || string.Equals(_currentSourceLanguage, DefaultSourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetTranslationEnabledForCurrentVideo(!IsLanguageCode(_currentSourceLanguage, _autoTranslatePreferredSourceLanguage));
    }

    private void SetTranslationEnabledForCurrentVideo(bool enabled)
    {
        _isTranslationEnabledForCurrentVideo = enabled;
    }

    private async Task ReprocessCurrentSubtitlesForTranslationSettingsAsync(CancellationToken cancellationToken)
    {
        if (_isTranslationEnabledForCurrentVideo && !HasSelectedTranslationModel())
        {
            ResetCurrentTranslations();
            PublishStatus("Select a translation model to start translating this video.");
            PublishSnapshot();
            await Task.CompletedTask;
            return;
        }

        if (_subtitleSource == SubtitlePipelineSource.Generated && !string.IsNullOrWhiteSpace(_currentVideoPath))
        {
            PublishStatus(
                _isTranslationEnabledForCurrentVideo
                    ? "Updating generated subtitle translation."
                    : "Showing source-language subtitles for the current video.");
            ResetCurrentTranslations();
            PublishSnapshot();
            await Task.CompletedTask;
            return;
        }

        if (_subtitleManager.HasCues)
        {
            ResetCurrentTranslations();
            PublishStatus(_isTranslationEnabledForCurrentVideo
                ? "Updating subtitle translation for the current video."
                : "Translation disabled for the current video.");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _translationCts?.Cancel();
            _translationCts?.Dispose();
            _translationCts = cts;
            _ = TranslateAllCuesAsync(cts.Token);
            PublishSnapshot();
            return;
        }

        PublishSnapshot();
        await Task.CompletedTask;
    }

    private async Task ReprocessCurrentSubtitlesForTranscriptionModelAsync(TranscriptionModelSelection selection, CancellationToken cancellationToken)
    {
        if (_subtitleSource == SubtitlePipelineSource.Generated && !string.IsNullOrWhiteSpace(_currentVideoPath))
        {
            if (TryLoadCachedGeneratedSubtitles(_currentVideoPath, selection.Key))
            {
                PublishStatus($"Loaded cached captions for {selection.DisplayName}.");
                await LoadSubtitleCuesAsync(
                    CloneCues(_subtitleManager.Cues),
                    SubtitlePipelineSource.Generated,
                    $"Loaded cached generated captions ({selection.DisplayName})",
                    cancellationToken,
                    preserveCurrentTranslationPreference: true);
                return;
            }

            PublishStatus($"Restarting transcription with {selection.DisplayName}.");
            await StartAutomaticCaptionGenerationAsync(_currentVideoPath, cancellationToken, preserveCurrentTranslationPreference: true);
            return;
        }

        PublishStatus($"Selected transcription model: {selection.DisplayName}.");
    }

    private void ResetCurrentTranslations()
    {
        lock (_translationSync)
        {
            foreach (var cue in _subtitleManager.Cues)
            {
                cue.TranslatedText = null;
            }

            _inFlightCueTranslations.Clear();
        }
    }

    private void RestoreTranslationSelection(string? previousModelKey)
    {
        _selectedTranslationModelKey = previousModelKey;
        if (string.IsNullOrWhiteSpace(previousModelKey))
        {
            _credentialFacade.ClearTranslationModelKey();
        }
        else
        {
            _credentialFacade.SaveTranslationModelKey(previousModelKey);
        }

        ConfigureTranslator();
        PublishSnapshot();
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
        _activeCaptionGenerationModelKey = null;
        _isCaptionGenerationInProgress = false;
    }

    private bool HasSelectedTranslationModel()
    {
        return !string.IsNullOrWhiteSpace(_selectedTranslationModelKey);
    }

    private void PublishLocalTranslationPreparationStatus()
    {
        if (!HasSelectedTranslationModel())
        {
            return;
        }

        var selection = SubtitleWorkflowCatalog.GetTranslationModel(_selectedTranslationModelKey);
        string? message = selection.Provider switch
        {
            TranslationProvider.LocalHyMt15_1_8B => "Starting HY-MT1.5 1.8B local translation. First use may download and load the model through llama.cpp.",
            TranslationProvider.LocalHyMt15_7B => "Starting HY-MT1.5 7B local translation. First use may download and load the model through llama.cpp.",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(_overlayStatusText))
        {
            PublishStatus(message, message);
        }
    }

    private void HandleSubtitleModelTransferProgress(ModelTransferProgress progress, int generationId)
    {
        if (generationId != _activeCaptionGenerationId)
        {
            return;
        }

        var message = FormatSubtitleModelTransferStatus(progress);
        PublishStatus(message, message);
    }

    private void HandleLocalTranslationRuntimeStatus(LocalTranslationRuntimeStatus status)
    {
        PublishStatus(status.Message, status.Message);
    }

    private void HandleLlamaRuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        RuntimeInstallProgressChanged?.Invoke(progress);

        var message = progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading llama.cpp runtime... {ratio:P0}."
                : "Downloading llama.cpp runtime...",
            "extracting" => progress.ProgressRatio is double ratio
                ? $"Extracting llama.cpp runtime... {ratio:P0}."
                : "Extracting llama.cpp runtime...",
            "ready" => "llama.cpp runtime is ready.",
            _ => "Preparing llama.cpp runtime..."
        };

        PublishStatus(message, message);
    }

    private void HandleFfmpegRuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        RuntimeInstallProgressChanged?.Invoke(progress);

        var message = progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading ffmpeg runtime... {ratio:P0}."
                : "Downloading ffmpeg runtime...",
            "extracting" => progress.ProgressRatio is double ratio
                ? $"Extracting ffmpeg runtime... {ratio:P0}."
                : "Extracting ffmpeg runtime...",
            "ready" => "ffmpeg runtime is ready.",
            _ => "Preparing ffmpeg runtime..."
        };

        PublishStatus(message, message);
    }

    private void PublishStatus(string message, string? overlayStatus = null)
    {
        if (overlayStatus is not null)
        {
            _overlayStatusText = overlayStatus;
        }

        StatusChanged?.Invoke(message);
        PublishSnapshot();
    }

    private void SetOverlayStatus(string? text)
    {
        _overlayStatusText = text;
    }

    private void PublishSnapshot()
    {
        SnapshotChanged?.Invoke(BuildSnapshot());
    }

    private static string FormatSubtitleModelTransferStatus(ModelTransferProgress progress)
    {
        var modelName = progress.ModelLabel switch
        {
            "TinyEn" => "Local Tiny.en",
            "BaseEn" => "Local Base.en",
            "SmallEn" => "Local Small.en",
            _ => progress.ModelLabel
        };

        return progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading {modelName} for subtitles... {ratio:P0}."
                : $"Downloading {modelName} for subtitles...",
            "loading" => $"Loading {modelName} for subtitles...",
            "ready" => $"{modelName} is ready. Generating captions...",
            _ => $"Preparing {modelName} for subtitles..."
        };
    }

    private string ApplySourceLanguageToCues(IReadOnlyList<SubtitleCue> cues)
    {
        var detectedLanguage = ResolveSourceLanguage(string.Join(" ", cues.Take(6).Select(cue => cue.SourceText)));
        foreach (var cue in cues)
        {
            cue.SourceLanguage ??= detectedLanguage;
        }

        return detectedLanguage;
    }

    private static string ResolveSourceLanguage(string text)
    {
        return LanguageDetector.Detect(text);
    }

    private static string ResolveAggregateSourceLanguage(string currentLanguage, string? nextLanguage)
    {
        if (string.IsNullOrWhiteSpace(nextLanguage) || string.Equals(nextLanguage, DefaultSourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return currentLanguage;
        }

        if (string.IsNullOrWhiteSpace(currentLanguage) || string.Equals(currentLanguage, DefaultSourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return nextLanguage;
        }

        if (IsEnglishLanguage(currentLanguage))
        {
            return nextLanguage;
        }

        return currentLanguage;
    }

    private static bool IsEnglishLanguage(string? languageCode)
    {
        return string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)
            || string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLanguageCode(string? actualLanguageCode, string? expectedLanguageCode)
    {
        if (string.IsNullOrWhiteSpace(actualLanguageCode) || string.IsNullOrWhiteSpace(expectedLanguageCode))
        {
            return false;
        }

        if (string.Equals(expectedLanguageCode, "en", StringComparison.OrdinalIgnoreCase))
        {
            return IsEnglishLanguage(actualLanguageCode);
        }

        return string.Equals(actualLanguageCode, expectedLanguageCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatClock(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private static void OpenExternalLink(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
