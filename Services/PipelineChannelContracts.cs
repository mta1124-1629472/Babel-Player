using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed record TranscriptChannelItem(
    string SegmentId,
    TranscriptSegmentArtifact Segment,
    string SourceLanguage,
    double LanguageProbability);

internal sealed record TranslationChannelItem(
    string SegmentId,
    TranslationSegmentArtifact Segment,
    string SourceLanguage,
    string TargetLanguage);

internal sealed record TtsChannelItem(
    string SegmentId,
    TranslationSegmentArtifact Segment,
    TtsResult Result);

public interface IStreamingTranscriptionProvider
{
    /// <summary>
        /// Starts a streaming transcription for the specified request and emits transcript updates to the provided channel writer.
        /// </summary>
        /// <param name="request">The transcription request describing the audio source, model options, and desired output parameters.</param>
        /// <param name="writer">A channel writer to which incremental <see cref="TranscriptChannelItem"/> updates will be written.</param>
        /// <param name="cancellationToken">A token to cancel the streaming operation; implementations must observe this token and stop producing new items when cancellation is requested.</param>
        /// <returns>The final <see cref="TranscriptionResult"/> containing the aggregated transcript, language information, and any session metrics.</returns>
        /// <remarks>
        /// Entry/exit state:
        /// - Entry: caller must ensure the hosting pipeline is initialized and ready to accept streaming transcription requests.
        /// - Exit on success: the transcription session is completed and the returned <see cref="TranscriptionResult"/> represents the final transcript state; implementations will have emitted final segment updates to <paramref name="writer"/>.
        /// Persistence:
        /// - Implementations may persist partial artifacts as items are written to <paramref name="writer"/> and should promote any partial artifacts to final storage before returning the final result.
        /// Cancellation:
        /// - When <paramref name="cancellationToken"/> is signaled, implementations should stop producing further transcript updates and perform any required cleanup or finalization as quickly as possible.
        /// </remarks>
        Task<TranscriptionResult> TranscribeStreamingAsync(
        TranscriptionRequest request,
        ChannelWriter<TranscriptChannelItem> writer,
        CancellationToken cancellationToken = default);
}

internal sealed class TranscriptChannelForwardingWriter(
    TranscriptArtifactStreamingWriter artifactWriter,
    ChannelWriter<TranscriptChannelItem> innerWriter) : ChannelWriter<TranscriptChannelItem>
{
    public override bool TryComplete(Exception? error = null) => innerWriter.TryComplete(error);

    public override bool TryWrite(TranscriptChannelItem item)
    {
        artifactWriter.AppendAsync(item, CancellationToken.None).GetAwaiter().GetResult();
        return innerWriter.TryWrite(item);
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
        innerWriter.WaitToWriteAsync(cancellationToken);

    public override async ValueTask WriteAsync(TranscriptChannelItem item, CancellationToken cancellationToken = default)
    {
        await artifactWriter.AppendAsync(item, cancellationToken).ConfigureAwait(false);
        await innerWriter.WriteAsync(item, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class TranscriptArtifactStreamingWriter
{
    private readonly string _partialPath;
    private TranscriptArtifact _artifact;

    public TranscriptArtifactStreamingWriter(
        string partialPath,
        string sourceLanguage,
        double languageProbability)
    {
        _partialPath = partialPath;
        _artifact = new TranscriptArtifact
        {
            Language = sourceLanguage,
            LanguageProbability = languageProbability,
            Segments = [],
        };
    }

    public string PartialPath => _partialPath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var dir = System.IO.Path.GetDirectoryName(_partialPath);
        if (!string.IsNullOrWhiteSpace(dir))
            System.IO.Directory.CreateDirectory(dir);

        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendAsync(TranscriptChannelItem item, CancellationToken cancellationToken)
    {
        _artifact.Language = item.SourceLanguage;
        _artifact.LanguageProbability = item.LanguageProbability;
        _artifact.Segments ??= [];
        _artifact.Segments.Add(CloneTranscriptSegment(item.Segment));
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(
        TranscriptionResult result,
        string finalPath,
        CancellationToken cancellationToken)
    {
        _artifact.Language = result.Language;
        _artifact.LanguageProbability = result.LanguageProbability;
        _artifact.PeakRamMb = result.PeakRamMb;
        _artifact.PeakVramMb = result.PeakVramMb;
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        PromotePartialFile(_partialPath, finalPath);
    }

    private Task PersistAsync(CancellationToken cancellationToken) =>
        System.IO.File.WriteAllTextAsync(_partialPath, ArtifactJson.SerializeTranscript(_artifact), cancellationToken);

    private static TranscriptSegmentArtifact CloneTranscriptSegment(TranscriptSegmentArtifact segment) =>
        new()
        {
            Start = segment.Start,
            End = segment.End,
            Text = segment.Text,
            SpeakerId = segment.SpeakerId,
            OriginalStart = segment.OriginalStart,
            Words = segment.Words is null ? null : [.. segment.Words],
        };

    public static string GetPartialPath(string finalPath) => $"{finalPath}.partial";

    public static void PromotePartialFile(string partialPath, string finalPath)
    {
        var dir = System.IO.Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(dir))
            System.IO.Directory.CreateDirectory(dir);

        if (System.IO.File.Exists(finalPath))
            System.IO.File.Delete(finalPath);

        System.IO.File.Move(partialPath, finalPath);
    }
}

internal sealed class TranslationArtifactStreamingWriter
{
    private readonly string _partialPath;
    private TranslationArtifact _artifact;

    public TranslationArtifactStreamingWriter(
        string partialPath,
        string sourceLanguage,
        string targetLanguage)
    {
        _partialPath = partialPath;
        _artifact = new TranslationArtifact
        {
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Segments = [],
        };
    }

    public string PartialPath => _partialPath;

    public IReadOnlyList<TranslationSegmentArtifact> OrderedSegments =>
        _artifact.Segments ?? [];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var dir = System.IO.Path.GetDirectoryName(_partialPath);
        if (!string.IsNullOrWhiteSpace(dir))
            System.IO.Directory.CreateDirectory(dir);

        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendPendingSegmentAsync(
        TranscriptChannelItem item,
        CancellationToken cancellationToken)
    {
        _artifact.SourceLanguage = item.SourceLanguage;
        _artifact.Segments ??= [];
        _artifact.Segments.Add(new TranslationSegmentArtifact
        {
            Id = item.SegmentId,
            Start = item.Segment.Start,
            End = item.Segment.End,
            Text = item.Segment.Text ?? string.Empty,
            TranslatedText = string.Empty,
            SpeakerId = item.Segment.SpeakerId,
        });

        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public int IndexOfSegment(string segmentId)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
            return -1;

        var segments = _artifact.Segments;
        if (segments is null)
            return -1;

        for (var i = 0; i < segments.Count; i++)
        {
            if (string.Equals(segments[i].Id, segmentId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    public async Task<TranslationSegmentArtifact> ApplyTranslatedTextAsync(
        string segmentId,
        string translatedText,
        string? sourceLanguage,
        string? targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
            throw new ArgumentException("Segment ID cannot be null or empty.", nameof(segmentId));

        _artifact.SourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage)
            ? _artifact.SourceLanguage
            : sourceLanguage;
        _artifact.TargetLanguage = string.IsNullOrWhiteSpace(targetLanguage)
            ? _artifact.TargetLanguage
            : targetLanguage;

        var segments = _artifact.Segments ?? [];
        TranslationSegmentArtifact? matched = null;
        foreach (var segment in segments)
        {
            if (!string.Equals(segment.Id, segmentId, StringComparison.Ordinal))
                continue;

            segment.TranslatedText = translatedText;
            matched = segment;
            break;
        }

        if (matched is null)
            throw new InvalidOperationException($"Translated segment '{segmentId}' was not found in the streaming translation artifact.");

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return CloneTranslationSegment(matched);
    }

    public async Task ReloadFromDiskAsync(CancellationToken cancellationToken)
    {
        _artifact = await ArtifactJson.LoadTranslationAsync(_partialPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(string finalPath, CancellationToken cancellationToken)
    {
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        TranscriptArtifactStreamingWriter.PromotePartialFile(_partialPath, finalPath);
    }

    private Task PersistAsync(CancellationToken cancellationToken) =>
        System.IO.File.WriteAllTextAsync(_partialPath, ArtifactJson.SerializeTranslation(_artifact), cancellationToken);

    private static TranslationSegmentArtifact CloneTranslationSegment(TranslationSegmentArtifact segment) =>
        new()
        {
            Id = segment.Id,
            Start = segment.Start,
            End = segment.End,
            Text = segment.Text,
            TranslatedText = segment.TranslatedText,
            SpeakerId = segment.SpeakerId,
        };
}
