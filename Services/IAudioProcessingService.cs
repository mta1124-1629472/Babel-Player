using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed record TimelineDubSegment(
    string SegmentId,
    string AudioPath,
    double StartSeconds,
    double SegmentDurationSeconds,
    bool TrimToSegmentWindow);

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
    /// Places generated per-segment TTS clips onto an absolute timeline and renders
    /// a single dubbed track.
    /// </summary>
    Task ComposeTimelineDubAsync(
        IReadOnlyList<TimelineDubSegment> segments,
        string outputAudioPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mixes a rendered dubbed track over a non-vocal ambiance/background bed.
    /// </summary>
    Task MixDubOverAmbianceAsync(
        string dubbedAudioPath,
        string ambianceAudioPath,
        string outputAudioPath,
        double ambianceGainDb,
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
    /// <summary>
        /// Extracts the complete audio track from the specified media file and writes it as a 16 kHz, single-channel (mono) WAV file.
        /// </summary>
        /// <param name="inputPath">Path to the source media file containing the audio track to extract.</param>
        /// <param name="outputPath">Destination path for the produced 16 kHz mono WAV file.</param>
        /// <param name="cancellationToken">Token to observe while waiting for the operation to complete; cancels the extraction when signaled.</param>
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
    /// <param name="maxRatio">Maximum acceptable tempo ratio (default 1.75 — 75% faster).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <summary>
        /// Adjusts the input audio's playback speed to match the specified target duration without changing pitch and writes the result to the output path when the required tempo ratio falls within the allowed range.
        /// </summary>
        /// <param name="inputPath">Path to the source media file to be time-stretched.</param>
        /// <param name="outputPath">Path where the stretched audio will be written; existing file will be overwritten when stretching occurs.</param>
        /// <param name="targetDurationSeconds">Desired duration, in seconds, of the resulting audio.</param>
        /// <param name="minRatio">Minimum allowed tempo ratio (output duration / input duration) to permit processing.</param>
        /// <param name="maxRatio">Maximum allowed tempo ratio (output duration / input duration) to permit processing.</param>
        /// <param name="cancellationToken">Token to observe for cancellation of the operation.</param>
        /// <returns>`true` if the audio was time-stretched and the output file was written; `false` if the computed tempo ratio is outside the specified [minRatio, maxRatio] bounds and no output is produced.</returns>
    Task<bool> TimeStretchAsync(
        string inputPath,
        string outputPath,
        double targetDurationSeconds,
        double minRatio = DubTimingDefaults.StretchMinTempoRatio,
        double maxRatio = DubTimingDefaults.StretchMaxTempoRatio,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the duration of an audio or video file using ffprobe.
    /// Returns null if ffprobe is unavailable or the file cannot be probed.
    /// <summary>
/// Probes the duration of an audio or video file using ffprobe.
/// </summary>
/// <param name="filePath">Path to the media file to probe.</param>
/// <param name="cancellationToken">Cancellation token to cancel the probing operation.</param>
/// <returns>The duration in seconds when probing succeeds; `null` if ffprobe is unavailable or probing fails.</returns>
    Task<double?> ProbeDurationAsync(string filePath, CancellationToken cancellationToken = default);
}
