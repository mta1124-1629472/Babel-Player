using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    // ── Diarization ───────────────────────────────────────────────────────────

    private async Task RunDiarizationAsync(string audioPath, string transcriptPath, CancellationToken ct)
    {
        if (DiarizationRegistry is null) return;

        var readiness = DiarizationRegistry.CheckReadiness(CurrentSettings.DiarizationProvider, CurrentSettings, KeyStore);
        if (!readiness.IsReady)
        {
            _log.Warning($"Diarization skipped: {readiness.BlockingReason}");
            return;
        }

        var provider = DiarizationRegistry.CreateProvider(CurrentSettings.DiarizationProvider, CurrentSettings, KeyStore);

        _log.Info($"Running diarization: provider={CurrentSettings.DiarizationProvider}, audio={audioPath}");
        var result = await provider.DiarizeAsync(new DiarizationRequest(audioPath), ct);

        if (!result.Success)
        {
            _log.Warning($"Diarization failed: {result.ErrorMessage}");
            return;
        }

        await MergeDiarizationIntoTranscriptAsync(transcriptPath, result.Segments, ct);

        CurrentSession = CurrentSession with
        {
            DiarizationProvider = CurrentSettings.DiarizationProvider,
            SpeakersDetectedAtUtc = DateTimeOffset.UtcNow,
        };
        SaveCurrentSession();

        _log.Info($"Diarization complete: {result.SpeakerCount} speakers across {result.Segments.Count} segments.");
    }

    private static async Task MergeDiarizationIntoTranscriptAsync(
        string transcriptPath,
        IReadOnlyList<DiarizedSegment> diarizedSegments,
        CancellationToken ct)
    {
        var artifact = await ArtifactJson.LoadTranscriptAsync(transcriptPath, ct);

        if (artifact.Segments is null) return;

        foreach (var segment in artifact.Segments)
            segment.SpeakerId = FindBestSpeakerFor(segment.Start, segment.End, diarizedSegments);

        var json = ArtifactJson.SerializeTranscript(artifact);
        await File.WriteAllTextAsync(transcriptPath, json, ct);
    }

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
            if (string.IsNullOrWhiteSpace(speakerId))
                return null;

            return CurrentSession.SpeakerReferenceAudioPaths.TryGetValue(speakerId, out var speakerPath) &&
                   !string.IsNullOrWhiteSpace(speakerPath)
                ? speakerPath
                : null;
        }

        return CurrentSession.SpeakerReferenceAudioPaths.TryGetValue(XttsReferenceKeys.SingleSpeakerDefault, out var defaultPath) &&
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

    private IMediaTransport GetOrCreateSegmentPlayer()
    {
        var player = _transportManager.GetOrCreateSegmentPlayer();
        player.PlaybackRate = TtsPlaybackRate;

        // Subscribe to segment lifecycle events exactly once.
        if (!_subscribedToSegmentEvents)
        {
            player.Ended += _segmentEndedHandler;
            player.ErrorOccurred += _segmentErrorHandler;
            _subscribedToSegmentEvents = true;
        }

        return player;
    }

    partial void OnTtsPlaybackRateChanged(double value)
    {
        if (_transportManager.SegmentPlayer is { } player)
            player.PlaybackRate = value;
    }

    public async Task PlayTtsForSegmentAsync(string segmentId)
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
        ActiveTtsSegmentId = segmentId;
        await Task.Run(() => player.Play());
    }

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

    public void StopPlayback()
    {
        StopTtsPlayback();
        StopSourceMedia();
    }

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
        await Task.Run(() => player.Play());
        _log.Info($"Playing source media at segment {segmentId} ({target.StartSeconds:F1}s)");
    }

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

        _transportManager.Dispose();
    }
}
