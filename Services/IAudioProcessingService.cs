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

    /// <summary>
    /// Extracts the full audio track from a media file as a 16 kHz mono WAV.
    /// </summary>
    /// <param name="inputPath">Source media path (video or audio).</param>
    /// <param name="outputPath">Target WAV path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExtractFullAudioAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Time-stretches an audio file to the target duration without changing pitch.
    /// Uses ffmpeg atempo; acceptable tempo range is [0.5, 2.0] (chained as needed).
    /// Returns false and leaves <paramref name="outputPath"/> absent when the stretch
    /// ratio falls outside <paramref name="minRatio"/>–<paramref name="maxRatio"/>.
    /// </summary>
    /// <param name="inputPath">Source audio file.</param>
    /// <param name="outputPath">Output audio file path (overwritten if it exists).</param>
    /// <param name="targetDurationSeconds">Desired output duration in seconds.</param>
    /// <param name="minRatio">Minimum acceptable tempo ratio (default 0.75 — 25% slower).</param>
    /// <param name="maxRatio">Maximum acceptable tempo ratio (default 1.35 — 35% faster).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the file was stretched and written; false if ratio is out of range.</returns>
    Task<bool> TimeStretchAsync(
        string inputPath,
        string outputPath,
        double targetDurationSeconds,
        double minRatio = 0.75,
        double maxRatio = 1.35,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the duration of an audio or video file using ffprobe.
    /// Returns null if ffprobe is unavailable or the file cannot be probed.
    /// </summary>
    Task<double?> ProbeDurationAsync(string filePath, CancellationToken cancellationToken = default);
}
