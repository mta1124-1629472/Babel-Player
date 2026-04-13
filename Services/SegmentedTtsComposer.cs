using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Runs a provider's single-segment TTS path over a translation artifact and combines the resulting
/// clips into one output file.
/// </summary>
public sealed class SegmentedTtsComposer
{
    private readonly IAudioProcessingService? _audioProcessingService;

    public SegmentedTtsComposer(IAudioProcessingService? audioProcessingService = null)
    {
        _audioProcessingService = audioProcessingService;
    }

    public async Task<TtsResult> GenerateAsync(
        TtsRequest request,
        AppLog log,
        string providerLabel,
        int maxConcurrency,
        Func<TranslationSegmentArtifact, string, SingleSegmentTtsRequest> requestFactory,
        Func<SingleSegmentTtsRequest, CancellationToken, Task<TtsResult>> generateSegmentAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TranslationJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VoiceName);

        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        log.Info($"[{providerLabel}] Starting combined TTS generation from {request.TranslationJsonPath}");

        var translationData = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);
        var candidateSegments = translationData.Segments?
            .Where(seg => !string.IsNullOrWhiteSpace(seg.Id) && !string.IsNullOrWhiteSpace(seg.TranslatedText))
            .ToList()
            ?? [];

        if (candidateSegments.Count == 0)
            throw new InvalidOperationException($"No valid segments with translated text found in {request.TranslationJsonPath}");

        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"babel-tts-{providerLabel.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var completed = 0;
            var segmentPaths = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var parallelism = Math.Max(1, Math.Min(maxConcurrency, candidateSegments.Count));

            await Parallel.ForEachAsync(
                candidateSegments,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken,
                },
                async (segment, ct) =>
                {
                    var segmentAudioPath = Path.Combine(tempDir, $"{segment.Id}.mp3");
                    var segmentRequest = requestFactory(segment, segmentAudioPath);
                    var segmentResult = await generateSegmentAsync(segmentRequest, ct);

                    if (!segmentResult.Success || !File.Exists(segmentAudioPath))
                        throw new InvalidOperationException($"Failed to generate TTS for segment {segment.Id}");

                    segmentPaths[segment.Id!] = segmentAudioPath;
                    request.SegmentProgress?.Report((Interlocked.Increment(ref completed), candidateSegments.Count));
                });

            var orderedPaths = candidateSegments
                .Select(segment => segmentPaths.TryGetValue(segment.Id!, out var path) ? path : null)
                .OfType<string>()
                .ToList();

            if (orderedPaths.Count != candidateSegments.Count)
                throw new InvalidOperationException("Combined TTS is missing one or more synthesized segment clips.");

            await CombineSegmentsAsync(orderedPaths, request.OutputAudioPath, log, cancellationToken);

            if (!File.Exists(request.OutputAudioPath))
                throw new InvalidOperationException($"Combined audio file was not created at {request.OutputAudioPath}");

            var fileSize = new FileInfo(request.OutputAudioPath).Length;
            log.Info($"[{providerLabel}] Combined TTS generation complete: {request.OutputAudioPath} ({fileSize} bytes)");
            return new TtsResult(true, request.OutputAudioPath, request.VoiceName, fileSize, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    public static string ResolveVoiceForSegment(TtsRequest request, TranslationSegmentArtifact segment)
    {
        if (!string.IsNullOrWhiteSpace(segment.SpeakerId)
            && request.SpeakerVoiceAssignments is not null
            && request.SpeakerVoiceAssignments.TryGetValue(segment.SpeakerId, out var mappedVoice)
            && !string.IsNullOrWhiteSpace(mappedVoice))
        {
            return mappedVoice;
        }

        return !string.IsNullOrWhiteSpace(request.DefaultVoiceFallback)
            ? request.DefaultVoiceFallback
            : request.VoiceName;
    }

    private async Task CombineSegmentsAsync(
        IReadOnlyList<string> segmentAudioPaths,
        string outputAudioPath,
        AppLog log,
        CancellationToken cancellationToken)
    {
        if (_audioProcessingService is not null)
        {
            await _audioProcessingService.CombineAudioSegmentsAsync(segmentAudioPaths, outputAudioPath, cancellationToken);
            return;
        }

        await AudioConcatUtility.CombineAudioSegmentsAsync(segmentAudioPaths, outputAudioPath, log, cancellationToken);
    }
}