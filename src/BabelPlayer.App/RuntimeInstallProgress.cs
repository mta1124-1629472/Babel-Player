namespace BabelPlayer.App;

public sealed class RuntimeInstallProgress
{
    public string Stage { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long? TotalBytes { get; init; }
    public int? ItemsCompleted { get; init; }
    public int? TotalItems { get; init; }

    public double? ProgressRatio =>
        TotalBytes is > 0
            ? (double)BytesTransferred / TotalBytes.Value
            : TotalItems is > 0 && ItemsCompleted is not null
                ? (double)ItemsCompleted.Value / TotalItems.Value
                : null;
}
