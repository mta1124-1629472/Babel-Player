namespace BabelPlayer.Core;

/// <summary>
/// Shared path and validation helpers for <see cref="IAudioExtractor"/> implementations.
/// </summary>
public static class AudioExtractorHelpers
{
    public static string MakeTempWavPath(string mediaPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "BabelPlayer");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(mediaPath)}-{Guid.NewGuid():N}.wav");
    }

    public static void ValidateInput(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
            throw new ArgumentNullException(nameof(mediaPath), "Media path cannot be null or empty.");
        if (!File.Exists(mediaPath))
            throw new FileNotFoundException($"Media file not found: {mediaPath}", mediaPath);
    }
}
