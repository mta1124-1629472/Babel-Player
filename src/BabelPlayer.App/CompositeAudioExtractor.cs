using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Tries each extractor in order, using the first one that reports
/// <see cref="IAudioExtractor.IsAvailable"/>.
/// Typical priority: MediaFoundation (Windows) → FFmpeg (all platforms).
/// </summary>
public sealed class CompositeAudioExtractor : IAudioExtractor
{
    private readonly IReadOnlyList<IAudioExtractor> _extractors;

    public CompositeAudioExtractor(params IAudioExtractor[] extractors)
    {
        if (extractors.Length == 0)
            throw new ArgumentException("At least one extractor must be provided.", nameof(extractors));
        _extractors = extractors;
    }

    public bool IsAvailable => _extractors.Any(e => e.IsAvailable);

    public string Extract(string mediaPath)
    {
        Exception? lastException = null;

        foreach (var extractor in _extractors)
        {
            if (!extractor.IsAvailable) continue;

            try
            {
                return extractor.Extract(mediaPath);
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw new InvalidOperationException(
            "All audio extractors failed or were unavailable.",
            lastException);
    }
}
