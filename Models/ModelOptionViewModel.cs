namespace Babel.Player.Models;

/// <summary>
/// Represents a model option in a dropdown, tracking whether it needs downloading.
/// </summary>
public sealed record ModelOptionViewModel(string ModelId, string? Label, bool? IsDownloaded)
{
    public bool IsReadyVisible => IsDownloaded == true;
    public bool IsDownloadVisible => IsDownloaded == false;

    public override string ToString() => Label ?? ModelId;
}
