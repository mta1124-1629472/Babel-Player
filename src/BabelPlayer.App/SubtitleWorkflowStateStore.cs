namespace BabelPlayer.App;

public sealed record SubtitleWorkflowState
{
    public string? CurrentVideoPath { get; init; }
    public string SelectedTranscriptionModelKey { get; init; } = SubtitleWorkflowCatalog.DefaultTranscriptionModelKey;
    public string? SelectedTranslationModelKey { get; init; }
    public bool IsTranslationEnabled { get; init; }
    public bool AutoTranslateEnabled { get; init; }
    public bool CurrentVideoTranslationPreferenceLocked { get; init; }
    public string CurrentSourceLanguage { get; init; } = "und";
    public string? OverlayStatus { get; init; }
    public string CaptionGenerationModeLabel { get; init; } = SubtitleWorkflowCatalog.GetTranscriptionModel(SubtitleWorkflowCatalog.DefaultTranscriptionModelKey).DisplayName;
    public int ActiveCaptionGenerationId { get; init; }
    public string? ActiveCaptionGenerationModelKey { get; init; }
}

public interface ISubtitleWorkflowStateStore
{
    event Action<SubtitleWorkflowState>? SnapshotChanged;

    SubtitleWorkflowState Snapshot { get; }
}

public sealed class InMemorySubtitleWorkflowStateStore : ISubtitleWorkflowStateStore
{
    private readonly object _sync = new();
    private SubtitleWorkflowState _snapshot = new();

    public event Action<SubtitleWorkflowState>? SnapshotChanged;

    public SubtitleWorkflowState Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot with { };
            }
        }
    }

    public void Update(Func<SubtitleWorkflowState, SubtitleWorkflowState> update)
    {
        SubtitleWorkflowState snapshot;
        lock (_sync)
        {
            _snapshot = update(_snapshot with { }) with { };
            snapshot = _snapshot with { };
        }

        SnapshotChanged?.Invoke(snapshot);
    }
}
