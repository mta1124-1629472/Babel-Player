namespace BabelPlayer.Core;

/// <summary>
/// Platform-agnostic contract for reading and writing application settings.
/// </summary>
public interface ISettingsStore
{
    string GetAppDataDirectory();
    void SaveLlamaCppRuntimeSource(string source);
    string? GetLlamaCppRuntimeSource();
}
