using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Provides audio processing capabilities such as concatenation and clipping.
/// Abstracting this allows unit tests to run in environments without ffmpeg.
/// </summary>
public interface IAudioProcessingService
{
    /// <summary>
    /// Concatenates multiple audio segment files into a single output audio file.
    /// </summary>
    /// <param name="segmentAudioPaths">Ordered list of input audio file paths.</param>
    /// <param name="outputAudioPath">Path to write the resulting audio file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CombineAudioSegmentsAsync(
        IReadOnlyList<string> segmentAudioPaths,
        string outputAudioPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extracts a portion of an audio or video file as a single-channel WAV file.
    /// </summary>
    /// <param name="inputPath">Source media path.</param>
    /// <param name="outputPath">Target audio path.</param>
    /// <param name="startTimeSeconds">Start time in seconds.</param>
    /// <param name="durationSeconds">Duration to extract in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExtractAudioClipAsync(
        string inputPath,
        string outputPath,
        double startTimeSeconds,
        double durationSeconds,
        CancellationToken cancellationToken);
}
