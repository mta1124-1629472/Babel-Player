using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    // ── Diarization ──────────────────────────────────────────────────────

    private readonly record struct DiarizationExecutionOutcome(
        bool SpeakerAssignmentsChanged,
        int SpeakerCount,
        int SegmentCount);

    /// <summary>
    /// Runs diarization on the current session's ingested media and merges detected speaker assignments into the transcript
    /// and optional translation, updating session state on success.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the diarization operation.</param>
    /// <returns>`true` if speaker assignments were changed in the transcript or translation, `false` otherwise.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the session is not at or beyond the Transcribed stage, when required session paths are missing or empty,
    /// or when no diarization provider is selected.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the ingested media file or the transcript file does not exist on disk.
    /// </exception>
    public async Task<bool> RunDiarizationAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Transcribed)
            throw new InvalidOperationException("No transcript available. Please transcribe media first.");

        if (string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath))
            throw new InvalidOperationException("No ingested media is available for diarization.");

        if (!File.Exists(CurrentSession.IngestedMediaPath))
            throw new FileNotFoundException($"Ingested media file not found: {CurrentSession.IngestedMediaPath}");

        if (string.IsNullOrWhiteSpace(CurrentSession.TranscriptPath))
            throw new InvalidOperationException("No transcript available. Please transcribe media first.");

        if (!File.Exists(CurrentSession.TranscriptPath))
            throw new FileNotFoundException($"Transcript file not found: {CurrentSession.TranscriptPath}");

        if (string.IsNullOrWhiteSpace(CurrentSettings.DiarizationProvider))
            throw new InvalidOperationException("No diarization provider is selected.");

        var outcome = await ExecuteDiarizationAsync(
            CurrentSession.IngestedMediaPath,
            CurrentSession.TranscriptPath,
            cancellationToken);

        return outcome.SpeakerAssignmentsChanged;
    }

    /// <summary>
    /// Executes speaker diarization for the specified audio file and merges detected speaker assignments into the transcript and optional translation artifacts.
    /// </summary>
    /// <param name="audioPath">Filesystem path to the source audio to diarize.</param>
    /// <param name="transcriptPath">Filesystem path to the transcript file to update with diarization speaker IDs.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="DiarizationExecutionOutcome"/> containing whether speaker assignments were applied to transcript/translation, the detected speaker count, and the diarized segment count.
    /// </returns>
    private async Task<DiarizationExecutionOutcome> ExecuteDiarizationAsync(
        string audioPath,
        string transcriptPath,
        CancellationToken ct)
    {
        if (DiarizationRegistry is null)
            throw new PipelineProviderException("No diarization registry is configured.");

        var providerDescriptor = DiarizationRegistry
            .GetAvailableProviders()
            .FirstOrDefault(provider => string.Equals(provider.Id, CurrentSettings.DiarizationProvider, StringComparison.Ordinal));
        var usesContainerizedRuntime = providerDescriptor?.EffectiveDefaultRuntime == InferenceRuntime.Containerized;

        var readiness = usesContainerizedRuntime && ContainerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckDiarizationForExecutionAsync(
                    CurrentSettings,
                    CurrentSettings.DiarizationProvider,
                    ContainerizedProbe,
                    ct)
                .ConfigureAwait(false)
            : DiarizationRegistry.CheckReadiness(CurrentSettings.DiarizationProvider, CurrentSettings, KeyStore);
        if (!readiness.IsReady)
        {
            var blockingReason = readiness.BlockingReason ?? "Diarization provider is not ready.";
            _log.Warning($"Diarization skipped: {blockingReason}");
            throw new PipelineProviderException(blockingReason);
        }

        ValidateDiarizationSpeakerBounds(
            CurrentSettings.DiarizationMinSpeakers,
            CurrentSettings.DiarizationMaxSpeakers);

        var provider = DiarizationRegistry.CreateProvider(CurrentSettings.DiarizationProvider, CurrentSettings, KeyStore);

        var request = new DiarizationRequest(
            SourceAudioPath:  audioPath,
            MinSpeakers:      CurrentSettings.DiarizationMinSpeakers,
            MaxSpeakers:      CurrentSettings.DiarizationMaxSpeakers);

        _log.Info($"Running diarization: provider={CurrentSettings.DiarizationProvider}, audio={audioPath}, " +
                  $"minSpeakers={CurrentSettings.DiarizationMinSpeakers?.ToString() ?? "auto"}, " +
                  $"maxSpeakers={CurrentSettings.DiarizationMaxSpeakers?.ToString() ?? "auto"}");

        var result = await provider.DiarizeAsync(request, ct);

        if (!result.Success)
        {
            _log.Warning($"Diarization failed: {result.ErrorMessage}");
            throw new InvalidOperationException(result.ErrorMessage ?? "Diarization provider returned an unsuccessful result.");
        }

        var transcriptChanged = await MergeDiarizationIntoTranscriptAsync(transcriptPath, result.Segments, ct);
        var translationChanged = false;

        if (!string.IsNullOrWhiteSpace(CurrentSession.TranslationPath) &&
            File.Exists(CurrentSession.TranslationPath))
        {
            translationChanged = await MergeSpeakerIdsIntoTranslationAsync(
                transcriptPath,
                CurrentSession.TranslationPath,
                ct);
        }

        CurrentSession = CurrentSession with
        {
            DiarizationProvider = CurrentSettings.DiarizationProvider,
            SpeakersDetectedAtUtc = DateTimeOffset.UtcNow,
        };
        SaveCurrentSession();

        _log.Info($"Diarization complete: {result.SpeakerCount} speakers across {result.Segments.Count} segments.");

        return new DiarizationExecutionOutcome(
            SpeakerAssignmentsChanged: transcriptChanged || translationChanged,
            SpeakerCount: result.SpeakerCount,
            SegmentCount: result.Segments.Count);
    }

    private void ValidateDiarizationSpeakerBounds(int? minSpeakers, int? maxSpeakers)
    {
        if (!minSpeakers.HasValue || !maxSpeakers.HasValue || minSpeakers.Value <= maxSpeakers.Value)
            return;

        var message =
            $"Invalid diarization speaker bounds: min speakers ({minSpeakers.Value}) cannot be greater than max speakers ({maxSpeakers.Value}).";
        _log.Warning(message);
        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Merges diarization speaker assignments into an existing transcript file, updating segment speaker IDs when they change.
    /// </summary>
    /// <param name="transcriptPath">Path to the transcript JSON file to read and potentially overwrite.</param>
    /// <param name="diarizedSegments">List of diarized speaker segments used to assign speaker IDs to transcript segments.</param>
    /// <param name="ct">Cancellation token to observe during I/O operations.</param>
    /// <returns>`true` if the transcript file was modified and written back to disk, `false` if no speaker assignments changed or the transcript had no segments.</returns>
    private static async Task<bool> MergeDiarizationIntoTranscriptAsync(
        string transcriptPath,
        IReadOnlyList<DiarizedSegment> diarizedSegments,
        CancellationToken ct)
    {
        var artifact = await ArtifactJson.LoadTranscriptAsync(transcriptPath, ct);
        if (artifact.Segments is null) return false;

        var before = CaptureTranscriptSpeakerAssignments(artifact.Segments);

        var result = new List<TranscriptSegmentArtifact>();
        foreach (var seg in artifact.Segments)
            result.AddRange(SplitSegmentAtSpeakerBoundaries(seg, diarizedSegments));

        var changed = !before.SequenceEqual(CaptureTranscriptSpeakerAssignments(result));
        if (!changed)
            return false;

        artifact.Segments.Clear();
        artifact.Segments.AddRange(result);

        var json = ArtifactJson.SerializeTranscript(artifact);
        await File.WriteAllTextAsync(transcriptPath, json, ct);
        return true;
    }

    /// <summary>
    /// Splits a transcript segment into one or more segments aligned to diarized speaker turns.
    /// </summary>
    /// <param name="segment">The transcript segment to split; its SpeakerId may be updated when no split is required.</param>
    /// <param name="diarized">A list of diarized speaker turns used to determine speaker boundaries and assignments.</param>
    /// <returns>
    /// A list of transcript segments covering the same time span as the input segment. If no speaker boundary requires splitting, the returned list contains the original segment (with SpeakerId set). If splits occur, each returned segment has Start/End, Text, Words, SpeakerId set and OriginalStart populated with the input segment's start.
    /// </returns>
    private static IReadOnlyList<TranscriptSegmentArtifact> SplitSegmentAtSpeakerBoundaries(
        TranscriptSegmentArtifact segment,
        IReadOnlyList<DiarizedSegment> diarized)
    {
        var overlapping = diarized
            .Where(d => d.EndSeconds > segment.Start && d.StartSeconds < segment.End)
            .OrderBy(d => d.StartSeconds)
            .ToList();

        // Single speaker or no word timestamps → assign best speaker, return as-is
        if (overlapping.Count <= 1 || segment.Words is null || segment.Words.Count == 0)
        {
            segment.SpeakerId = FindBestSpeakerFor(segment.Start, segment.End, diarized);
            return [segment];
        }

        // Group consecutive words by which diarized speaker turn they fall in
        var groups = new List<(string SpeakerId, List<WordTimestamp> Words)>();
        foreach (var word in segment.Words)
        {
            var wordMid = (word.Start + word.End) / 2.0;
            var speaker = overlapping
                .FirstOrDefault(d => d.StartSeconds <= wordMid && d.EndSeconds > wordMid)
                ?.SpeakerId
                ?? FindBestSpeakerFor(word.Start, word.End, diarized);

            if (groups.Count == 0 || groups[^1].SpeakerId != speaker)
                groups.Add((speaker, []));
            groups[^1].Words.Add(word);
        }

        // All words landed on one speaker after word-level assignment → no split needed
        if (groups.Count == 1)
        {
            segment.SpeakerId = groups[0].SpeakerId;
            return [segment];
        }

        return groups.Select(g => new TranscriptSegmentArtifact
        {
            Start         = g.Words[0].Start,
            End           = g.Words[^1].End,
            Text          = string.Join("", g.Words.Select(w => w.Text)).Trim(),
            SpeakerId     = g.SpeakerId,
            Words         = g.Words,
            OriginalStart = segment.Start,
        }).ToList<TranscriptSegmentArtifact>();
    }

    /// <summary>
    /// Merge speaker IDs from a transcript artifact into a translation artifact when segment start times align.
    /// </summary>
    /// <param name="transcriptPath">Filesystem path to the transcript JSON artifact.</param>
    /// <param name="translationPath">Filesystem path to the translation JSON artifact to update.</param>
    /// <param name="ct">Cancellation token for the asynchronous file and I/O operations.</param>
    /// <returns>`true` if one or more translation segments had their `SpeakerId` changed and the translation file was written; `false` if no changes were made or either artifact had no segments.</returns>
    private static async Task<bool> MergeSpeakerIdsIntoTranslationAsync(
        string transcriptPath,
        string translationPath,
        CancellationToken ct)
    {
        var transcript = await ArtifactJson.LoadTranscriptAsync(transcriptPath, ct);
        var translation = await ArtifactJson.LoadTranslationAsync(translationPath, ct);

        if (transcript.Segments is null || translation.Segments is null) return false;

        // Build lookup keyed by OriginalStart (set on split segments) or Start.
        // For split segments that share the same OriginalStart, the first entry wins
        // so the translation segment (which uses the pre-split start time) is matched.
        var speakerByStart = new Dictionary<double, string>();
        foreach (var s in transcript.Segments)
        {
            if (s.SpeakerId is null) continue;
            var key = s.OriginalStart ?? s.Start;
            speakerByStart.TryAdd(key, s.SpeakerId);
        }

        var anyChanged = false;
        foreach (var seg in translation.Segments)
        {
            if (!speakerByStart.TryGetValue(seg.Start, out var speakerId)) continue;
            if (seg.SpeakerId == speakerId) continue;
            seg.SpeakerId = speakerId;
            anyChanged = true;
        }

        if (!anyChanged) return false;

        var json = ArtifactJson.SerializeTranslation(translation);
        await File.WriteAllTextAsync(translationPath, json, ct);
        return true;
    }

    /// <summary>
    /// Produces a comparable list of speaker-assignment tuples extracted from transcript segments.
    /// </summary>
    /// <param name="segments">The transcript segments to capture assignments from.</param>
    /// <returns>
    /// A list of tuples for each segment containing its Start, End, OriginalStart (may be null), and SpeakerId (empty string if the segment's SpeakerId is null).
    /// </returns>
    private static IReadOnlyList<(double Start, double End, double? OriginalStart, string SpeakerId)> CaptureTranscriptSpeakerAssignments(
        IReadOnlyList<TranscriptSegmentArtifact> segments)
    {
        var result = new List<(double Start, double End, double? OriginalStart, string SpeakerId)>(segments.Count);
        foreach (var segment in segments)
        {
            result.Add((
                segment.Start,
                segment.End,
                segment.OriginalStart,
                segment.SpeakerId ?? string.Empty));
        }

        return result;
    }

    /// <summary>
    /// Selects the speaker ID whose diarized interval has the largest overlap with the specified time range.
    /// </summary>
    /// <param name="start">Start time of the range in seconds.</param>
    /// <param name="end">End time of the range in seconds.</param>
    /// <param name="diarizedSegments">Diarized segments to consider (each with start/end times and a SpeakerId).</param>
    /// <returns>The speaker ID with the greatest positive overlap, or "spk_00" if no diarized segment overlaps the range.</returns>
    private static string FindBestSpeakerFor(double start, double end, IReadOnlyList<DiarizedSegment> diarizedSegments)
    {
        string? best = null;
        double bestOverlap = 0;
        foreach (var d in diarizedSegments)
        {
            var overlapStart = Math.Max(start, d.StartSeconds);
            var overlapEnd = Math.Min(end, d.EndSeconds);
            var overlap = Math.Max(0, overlapEnd - overlapStart);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = d.SpeakerId;
            }
        }
        return best ?? "spk_00";
    }

    public IReadOnlyDictionary<string, string> GetSpeakerVoiceAssignments() =>
        CurrentSession.SpeakerVoiceAssignments is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(CurrentSession.SpeakerVoiceAssignments, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> GetSpeakerReferenceAudioPaths() =>
        CurrentSession.SpeakerReferenceAudioPaths is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(CurrentSession.SpeakerReferenceAudioPaths, StringComparer.Ordinal);

    public void SetSpeakerVoiceAssignment(string speakerId, string voiceOrModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceOrModel);

        var current = CurrentSession.SpeakerVoiceAssignments ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var updated = new Dictionary<string, string>(current, StringComparer.Ordinal)
        {
            [speakerId] = voiceOrModel,
        };

        CurrentSession = CurrentSession with { SpeakerVoiceAssignments = updated };
        SaveCurrentSession();
    }

    public void RemoveSpeakerVoiceAssignment(string speakerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        if (CurrentSession.SpeakerVoiceAssignments is null)
            return;

        var updated = new Dictionary<string, string>(CurrentSession.SpeakerVoiceAssignments, StringComparer.Ordinal);
        if (!updated.Remove(speakerId))
            return;

        CurrentSession = CurrentSession with { SpeakerVoiceAssignments = updated.Count == 0 ? null : updated };
        SaveCurrentSession();
    }

    public void SetSpeakerReferenceAudioPath(string speakerId, string clipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);

        var current = CurrentSession.SpeakerReferenceAudioPaths ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var updated = new Dictionary<string, string>(current, StringComparer.Ordinal)
        {
            [speakerId] = clipPath,
        };

        CurrentSession = CurrentSession with { SpeakerReferenceAudioPaths = updated };
        SaveCurrentSession();
    }

    public void RemoveSpeakerReferenceAudioPath(string speakerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        if (CurrentSession.SpeakerReferenceAudioPaths is null)
            return;

        var updated = new Dictionary<string, string>(CurrentSession.SpeakerReferenceAudioPaths, StringComparer.Ordinal);
        if (!updated.Remove(speakerId))
            return;

        CurrentSession = CurrentSession with { SpeakerReferenceAudioPaths = updated.Count == 0 ? null : updated };
        SaveCurrentSession();
    }

    public void SetMultiSpeakerEnabled(bool enabled)
    {
        if (CurrentSession.MultiSpeakerEnabled == enabled)
            return;

        CurrentSession = CurrentSession with { MultiSpeakerEnabled = enabled };
        SaveCurrentSession();
    }

    public void SetDefaultTtsVoiceFallback(string? voice)
    {
        var normalized = string.IsNullOrWhiteSpace(voice) ? null : voice.Trim();
        if (string.Equals(CurrentSession.DefaultTtsVoiceFallback, normalized, StringComparison.Ordinal))
            return;

        CurrentSession = CurrentSession with { DefaultTtsVoiceFallback = normalized };
        SaveCurrentSession();
    }

    private string ResolveVoiceForSegment(TranslationSegmentArtifact segment, string defaultVoice)
    {
        if (!CurrentSession.MultiSpeakerEnabled)
            return defaultVoice;

        var speakerId = segment.SpeakerId;
        if (string.IsNullOrWhiteSpace(speakerId))
            return !string.IsNullOrWhiteSpace(CurrentSession.DefaultTtsVoiceFallback)
                ? CurrentSession.DefaultTtsVoiceFallback
                : defaultVoice;

        if (CurrentSession.SpeakerVoiceAssignments is not null &&
            CurrentSession.SpeakerVoiceAssignments.TryGetValue(speakerId, out var mappedVoice) &&
            !string.IsNullOrWhiteSpace(mappedVoice))
        {
            return mappedVoice;
        }

        return !string.IsNullOrWhiteSpace(CurrentSession.DefaultTtsVoiceFallback)
            ? CurrentSession.DefaultTtsVoiceFallback
            : defaultVoice;
    }

    private string? ResolveReferenceAudioForSegment(TranslationSegmentArtifact segment)
    {
        if (CurrentSession.SpeakerReferenceAudioPaths is null)
            return null;

        if (CurrentSession.MultiSpeakerEnabled)
        {
            var speakerId = segment.SpeakerId;
            if (!string.IsNullOrWhiteSpace(speakerId) &&
                CurrentSession.SpeakerReferenceAudioPaths.TryGetValue(speakerId, out var speakerPath) &&
                !string.IsNullOrWhiteSpace(speakerPath))
                return speakerPath;

            // No per-speaker reference — fall back to the provider's default key so the
            // provider's auto-extract fallback (or a manually placed default) can still fire.
            var fallbackKey = QwenReferenceKeys.SingleSpeakerDefault;
            return CurrentSession.SpeakerReferenceAudioPaths.TryGetValue(fallbackKey, out var fallbackPath) &&
                   !string.IsNullOrWhiteSpace(fallbackPath)
                ? fallbackPath
                : null;
        }

        var defaultKey = QwenReferenceKeys.SingleSpeakerDefault;
        return CurrentSession.SpeakerReferenceAudioPaths.TryGetValue(defaultKey, out var defaultPath) &&
               !string.IsNullOrWhiteSpace(defaultPath)
            ? defaultPath
            : null;
    }

    private void QueueMediaReloadRequest(bool autoPlay, string reason)
    {
        if (string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath))
            return;

        PendingMediaReloadRequest = new MediaReloadRequest(
            CurrentSession.IngestedMediaPath,
            autoPlay,
            reason);
    }

    /// <summary>
    /// Retrieves the segment player used for TTS playback, creating one if necessary.
    /// </summary>
    /// <returns>The segment player instance; its <c>PlaybackRate</c> is set to the coordinator's TTS playback rate and segment lifecycle event handlers are subscribed (only once).</returns>
    private IMediaTransport GetOrCreateSegmentPlayer()
    {
        var player = _transportManager.GetOrCreateSegmentPlayer();
        player.PlaybackRate = TtsPlaybackRate;
        player.Volume = TtsVolume;

        // Subscribe to segment lifecycle events exactly once.
        if (!_subscribedToSegmentEvents)
        {
            player.Ended += _segmentEndedHandler;
            player.ErrorOccurred += _segmentErrorHandler;
            _subscribedToSegmentEvents = true;
        }

        return player;
    }

    /// <summary>
    /// Update the segment player's playback rate to reflect a changed TTS playback rate.
    /// </summary>
    /// <summary>
    /// Update the segment player's playback rate to match the new TTS playback rate.
    /// </summary>
    /// <param name="value">New playback rate multiplier (e.g., 1.0 = normal speed).</param>
    partial void OnTtsPlaybackRateChanged(double value)
    {
        if (_transportManager.SegmentPlayer is { } player)
            player.PlaybackRate = value;
    }

    partial void OnTtsVolumeChanged(double value)
    {
        if (_transportManager.SegmentPlayer is { } player)
            player.Volume = value;
    }

    /// <summary>
    /// Starts TTS playback for the specified segment by loading its audio and scheduling playback.
    /// </summary>
    /// <param name="segmentId">The identifier of the segment whose TTS audio should be played.</param>
    /// <returns>A Task that completes after playback has been scheduled.</returns>
    /// <exception cref="InvalidOperationException">Thrown if there is no active session or if no TTS audio path exists for the given segment.</exception>
    /// <summary>
    /// Plays the TTS audio associated with the specified segment and schedules playback.
    /// </summary>
    /// <param name="segmentId">Identifier of the segment whose TTS audio will be played.</param>
    /// <returns>A task that completes when playback has been scheduled.</returns>
    /// <exception cref="InvalidOperationException">Thrown if there is no active session or if no TTS audio path exists for the specified segment.</exception>
    /// <exception cref="FileNotFoundException">Thrown if the resolved TTS audio file does not exist on disk.</exception>
    public Task PlayTtsForSegmentAsync(string segmentId)
    {
        if (CurrentSession is null)
            throw new InvalidOperationException("No active session.");

        var paths = CurrentSession.TtsSegmentAudioPaths;
        if (paths is null || !paths.TryGetValue(segmentId, out var audioPath))
            throw new InvalidOperationException($"No TTS audio path for segment '{segmentId}'.");

        if (!File.Exists(audioPath))
            throw new FileNotFoundException($"TTS audio file not found: {audioPath}", audioPath);

        StopTtsPlayback();
        PlaybackState = PlaybackState.PlayingSingleSegment;

        var player = GetOrCreateSegmentPlayer();
        player.Load(audioPath);
        player.Volume = TtsVolume;
        ActiveTtsSegmentId = segmentId;
        _ = Task.Run(() => player.Play()).FireAndForgetAsync(_log, $"Play TTS for segment {segmentId}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops any active TTS playback and resets the coordinator's TTS playback state.
    /// </summary>
    /// <remarks>
    /// If a segment player exists, attempts to pause it and ignores an ObjectDisposedException (race/shutdown case).
    /// After returning, <see cref="ActiveTtsSegmentId"/> is cleared and <see cref="PlaybackState"/> is set to <see cref="PlaybackState.Idle"/>.
    /// </remarks>
    public void StopTtsPlayback()
    {
        try
        {
            _transportManager.SegmentPlayer?.Pause();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown/race path: segment transport was disposed while timer tick tried to stop playback.
        }
        ActiveTtsSegmentId = null;
        PlaybackState = PlaybackState.Idle;
    }

    /// <summary>
    /// Stops any active TTS playback and pauses the source media player.
    /// </summary>
    public void StopPlayback()
    {
        StopTtsPlayback();
        StopSourceMedia();
    }

    /// <summary>
    /// Start playback of the ingested source media positioned at the start time of the specified segment.
    /// </summary>
    /// <param name="segmentId">Identifier of the segment whose start time will be used as the seek target.</param>
    /// <returns>A task that completes after playback has been scheduled.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when there is no active session, when no media is loaded, or when the specified segment cannot be found.
    /// </exception>
    /// <summary>
    /// Starts playback of the session's ingested media positioned at the start time of the specified segment.
    /// </summary>
    /// <param name="segmentId">The identifier of the segment to play from.</param>
    /// <returns>A task that completes after playback has been scheduled.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when there is no active session, no media is loaded, or the specified segment cannot be found.
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the ingested media file does not exist at the recorded path.</exception>
    public async Task PlaySourceMediaAtSegmentAsync(string segmentId)
    {
        if (CurrentSession is null)
            throw new InvalidOperationException("No active session.");

        if (string.IsNullOrEmpty(CurrentSession.IngestedMediaPath))
            throw new InvalidOperationException("No media loaded.");

        if (!File.Exists(CurrentSession.IngestedMediaPath))
            throw new FileNotFoundException($"Ingested media not found: {CurrentSession.IngestedMediaPath}");

        var segments = await GetSegmentWorkflowListAsync();
        var target = segments.Find(s => s.SegmentId == segmentId);
        if (target is null)
            throw new InvalidOperationException($"Segment not found: {segmentId}");

        var player = GetOrCreateSourcePlayer();
        player.Load(CurrentSession.IngestedMediaPath);
        player.Seek((long)(target.StartSeconds * 1000));
        _log.Info($"Playing source media at segment {segmentId} ({target.StartSeconds:F1}s)");
        _ = Task.Run(() => player.Play()).FireAndForgetAsync(_log, $"Play Source Media at segment {segmentId}");
    }

    /// <summary>
    /// Pauses playback of the currently loaded source media, if a source media player exists.
    /// <summary>
    /// Pauses playback of the current source media player if one exists.
    /// </summary>
    public void StopSourceMedia()
    {
        _transportManager.SourceMediaPlayer?.Pause();
    }

    public IMediaTransport GetOrCreateSourcePlayer() =>
        GetOrCreateSourcePlayerWithDiagnostics();

    private IMediaTransport GetOrCreateSourcePlayerWithDiagnostics()
    {
        var player = _transportManager.GetOrCreateSourcePlayer();
        EnsureSourcePlayerDiagnosticsSubscribed(player);
        return player;
    }

    public IMediaTransport? SourceMediaPlayer => _transportManager.SourceMediaPlayer;

    /// <summary>The TTS segment player, if it has been created. Null until first TTS playback.</summary>
    public IMediaTransport? SegmentPlayer => _transportManager.SegmentPlayer;

    /// <summary>
    /// Performs an orderly shutdown by flushing pending state, unsubscribing event handlers, waiting for in-flight TTS tasks, and disposing managed resources.
    /// </summary>
    /// <remarks>
    /// Attempts to complete any pending save and in-flight TTS operations before disposing internal services and transport resources. Exceptions thrown during disposal or while waiting for pending tasks are caught and ignored to allow shutdown to continue.
    /// </remarks>
    public void Dispose()
    {
        FlushPendingSave();

        // Unsubscribe segment events before disposing the transport manager.
        if (_subscribedToSegmentEvents)
        {
            var segmentPlayer = _transportManager.GetOrCreateSegmentPlayer();
            segmentPlayer.Ended -= _segmentEndedHandler;
            segmentPlayer.ErrorOccurred -= _segmentErrorHandler;
            _subscribedToSegmentEvents = false;
        }

        if (_subscribedToSourceDiagnostics
            && _transportManager.SourceMediaPlayer is LibMpvEmbeddedTransport embedded)
        {
            embedded.VsrDiagnosticChanged -= _vsrDiagnosticChangedHandler;
            _subscribedToSourceDiagnostics = false;
        }

        if (_containerizedInferenceManager is IDisposable disposableInferenceManager)
        {
            try
            {
                disposableInferenceManager.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to dispose containerized inference manager on shutdown: {ex.Message}");
            }
        }

        // Wait for all in-flight TTS operations to complete before disposing the TTS service
        // to avoid killing a shared HttpClient mid-request.
        if (_pendingTtsTasks.Count > 0)
        {
            try
            {
                Task.WhenAll(_pendingTtsTasks).Wait();
            }
            catch
            {
                // Ignore exceptions during shutdown - tasks may have been canceled or failed.
            }
        }

        (_ttsService as IDisposable)?.Dispose();
        _transportManager.Dispose();
    }
}
