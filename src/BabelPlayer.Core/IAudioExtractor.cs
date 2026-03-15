namespace BabelPlayer.Core;

/// <summary>
/// Platform-agnostic contract for extracting a 16 kHz mono 16-bit PCM WAV
/// from any media file, suitable for ASR processing.
/// </summary>
public interface IAudioExtractor
{
    /// <summary>Returns true when this extractor can run on the current platform/environment.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Extracts audio from <paramref name="mediaPath"/> and writes a temporary WAV file.
    /// The caller is responsible for deleting the returned path when done.
    /// </summary>
    string Extract(string mediaPath);
}
