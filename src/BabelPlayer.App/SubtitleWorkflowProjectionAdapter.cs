namespace BabelPlayer.App;

public sealed class SubtitleWorkflowProjectionAdapter : IDisposable
{
    private readonly ISubtitleWorkflowStateStore _workflowStateStore;
    private readonly IMediaSessionStore _mediaSessionStore;

    public SubtitleWorkflowProjectionAdapter(
        ISubtitleWorkflowStateStore workflowStateStore,
        IMediaSessionStore mediaSessionStore)
    {
        _workflowStateStore = workflowStateStore;
        _mediaSessionStore = mediaSessionStore;
        Current = BuildSnapshot(_workflowStateStore.Snapshot, _mediaSessionStore.Snapshot);
        _workflowStateStore.SnapshotChanged += HandleStateChanged;
        _mediaSessionStore.SnapshotChanged += HandleSessionChanged;
    }

    public event Action<SubtitleWorkflowSnapshot>? SnapshotChanged;

    public SubtitleWorkflowSnapshot Current { get; private set; }

    public void Dispose()
    {
        _workflowStateStore.SnapshotChanged -= HandleStateChanged;
        _mediaSessionStore.SnapshotChanged -= HandleSessionChanged;
    }

    private void HandleStateChanged(SubtitleWorkflowState state)
    {
        Current = BuildSnapshot(state, _mediaSessionStore.Snapshot);
        SnapshotChanged?.Invoke(Current);
    }

    private void HandleSessionChanged(MediaSessionSnapshot session)
    {
        Current = BuildSnapshot(_workflowStateStore.Snapshot, session);
        SnapshotChanged?.Invoke(Current);
    }

    private static SubtitleWorkflowSnapshot BuildSnapshot(SubtitleWorkflowState state, MediaSessionSnapshot session)
    {
        return new SubtitleWorkflowSnapshot
        {
            CurrentVideoPath = state.CurrentVideoPath ?? session.Source.Path,
            SelectedTranscriptionModelKey = state.SelectedTranscriptionModelKey,
            SelectedTranscriptionLabel = SubtitleWorkflowCatalog.GetTranscriptionModel(state.SelectedTranscriptionModelKey).DisplayName,
            SelectedTranslationModelKey = state.SelectedTranslationModelKey,
            SelectedTranslationLabel = SubtitleWorkflowCatalog.GetTranslationModel(state.SelectedTranslationModelKey).DisplayName,
            IsTranslationEnabled = session.Translation.IsEnabled,
            AutoTranslateEnabled = session.Translation.AutoTranslateEnabled,
            IsCaptionGenerationInProgress = session.Transcript.IsGenerating,
            CurrentSourceLanguage = session.LanguageAnalysis.CurrentSourceLanguage,
            SubtitleSource = session.Transcript.Source,
            OverlayStatus = state.OverlayStatus ?? session.SubtitlePresentation.StatusText,
            ActiveCue = MediaSessionProjection.ToActiveCue(session),
            Cues = MediaSessionProjection.ToSubtitleCues(session),
            CaptionGenerationModeLabel = state.CaptionGenerationModeLabel
        };
    }
}
