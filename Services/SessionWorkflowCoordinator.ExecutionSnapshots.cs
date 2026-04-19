using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Planning;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    internal long SettingsRevision => System.Threading.Interlocked.Read(ref _settingsRevision);

    internal long SessionRevision => System.Threading.Interlocked.Read(ref _sessionRevision);

    private void MarkSettingsInputsChanged(string reason)
    {
        var next = System.Threading.Interlocked.Increment(ref _settingsRevision);
        _log.Debug($"Settings revision advanced to {next}: {reason}");
    }

    private void MarkSessionInputsChanged(string reason)
    {
        var next = System.Threading.Interlocked.Increment(ref _sessionRevision);
        _log.Debug($"Session revision advanced to {next}: {reason}");
    }

    private void RetireTranslationProviderCache(string reason)
    {
        _translationProviderLeases.RetireCurrent(reason);
        _translationService = null;
    }

    private void RetireTtsProviderCache(string reason)
    {
        _ttsProviderLeases.RetireCurrent(reason);
        _ttsService = null;
    }

    private ProviderLease<ITranslationProvider> AcquireTranslationProviderLease(string providerId)
    {
        var lease = _translationProviderLeases.AcquireOrCreate(CreateTranslationService, providerId);
        _translationService = _translationProviderLeases.CurrentProvider;
        return lease;
    }

    private ProviderLease<ITtsProvider> AcquireTtsProviderLease(string providerId)
    {
        var lease = _ttsProviderLeases.AcquireOrCreate(CreateTtsService, providerId);
        _ttsService = _ttsProviderLeases.CurrentProvider;
        return lease;
    }

    private async Task<TranslationExecutionSnapshot> PrepareTranslationExecutionSnapshotAsync(
        StageExecutionPlan stagePlan,
        string transcriptPath,
        string normalizedSourceLanguage,
        string normalizedTargetLanguage,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await EnsureTranslationExecutionReadyAsync(progress, cancellationToken).ConfigureAwait(false);
        var providerLease = AcquireTranslationProviderLease(stagePlan.ProviderId);

        var sessionDir = GetSessionDirectory();
        var translationDir = Path.Combine(sessionDir, "translations");
        Directory.CreateDirectory(translationDir);
        var fileName = Path.GetFileNameWithoutExtension(transcriptPath);
        var translationPath = Path.Combine(translationDir, $"{fileName}_{normalizedTargetLanguage}.json");

        return new TranslationExecutionSnapshot(
            Guid.NewGuid(),
            CurrentSession.SessionId,
            SettingsRevision,
            SessionRevision,
            stagePlan,
            providerLease,
            CurrentSettings.TranslationModel,
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            transcriptPath,
            ArtifactIdentity.Capture(transcriptPath),
            translationPath,
            ArtifactIntegrity.GetWorkingPath(translationPath));
    }

    private async Task<TtsExecutionSnapshot> PrepareTtsExecutionSnapshotAsync(
        StageExecutionPlan stagePlan,
        string translationPath,
        string voice,
        string? ttsLanguage,
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        await EnsureTtsProviderReadyAsync(voice, progress, stageContext, cancellationToken).ConfigureAwait(false);
        var providerLease = AcquireTtsProviderLease(stagePlan.ProviderId);

        await EnsureSingleSpeakerQwenReferenceClipAsync(stagePlan.ProviderId, cancellationToken).ConfigureAwait(false);
        await EnsureMultiSpeakerReferenceClipsAsync(stagePlan.ProviderId, cancellationToken).ConfigureAwait(false);

        var (ttsPath, segmentsDir) = BuildTtsOutputPaths(translationPath, voice);
        var speakerVoiceAssignments = CurrentSession.SpeakerVoiceAssignments is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(CurrentSession.SpeakerVoiceAssignments, StringComparer.Ordinal);
        var speakerReferencePaths = CurrentSession.SpeakerReferenceAudioPaths is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(CurrentSession.SpeakerReferenceAudioPaths, StringComparer.Ordinal);
        var timingOverrides = CurrentSession.SegmentTimingModeOverrides is null
            ? new Dictionary<string, SegmentTimingMode>(StringComparer.Ordinal)
            : new Dictionary<string, SegmentTimingMode>(CurrentSession.SegmentTimingModeOverrides, StringComparer.Ordinal);

        return new TtsExecutionSnapshot(
            Guid.NewGuid(),
            CurrentSession.SessionId,
            SettingsRevision,
            SessionRevision,
            stagePlan,
            providerLease,
            voice,
            ttsLanguage,
            translationPath,
            ArtifactIdentity.Capture(translationPath),
            ttsPath,
            segmentsDir,
            CurrentSession.IngestedMediaPath ?? CurrentSession.SourceMediaPath,
            CurrentSession.AmbianceAudioPath,
            CurrentSettings.DubTimingMode,
            CurrentSettings.AmbianceMixDb,
            timingOverrides,
            speakerVoiceAssignments,
            speakerReferencePaths,
            CurrentSession.DefaultTtsVoiceFallback);
    }

    private bool TranslationInputsStillMatch(TranslationExecutionSnapshot snapshot)
    {
        lock (_sessionLock)
        {
            return CurrentSession.SessionId == snapshot.SessionId
                && snapshot.TranscriptIdentity.Matches(CurrentSession.TranscriptPath);
        }
    }

    private bool TtsInputsStillMatch(TtsExecutionSnapshot snapshot)
    {
        lock (_sessionLock)
        {
            return CurrentSession.SessionId == snapshot.SessionId
                && SessionRevision == snapshot.SessionRevision
                && snapshot.TranslationIdentity.Matches(CurrentSession.TranslationPath)
                && DictionariesEqual(CurrentSession.SpeakerVoiceAssignments, snapshot.SpeakerVoiceAssignments)
                && DictionariesEqual(CurrentSession.SpeakerReferenceAudioPaths, snapshot.SpeakerReferenceAudioPaths)
                && DictionariesEqual(CurrentSession.SegmentTimingModeOverrides, snapshot.SegmentTimingOverrides);
        }
    }

    private bool TranslationSettingsDrifted(TranslationExecutionSnapshot snapshot) =>
        CurrentSettings.TranslationRuntime != snapshot.Plan.Runtime
        || CurrentSettings.TranslationProfile != snapshot.Plan.Profile
        || !string.Equals(CurrentSettings.TranslationProvider, snapshot.Plan.ProviderId, StringComparison.Ordinal)
        || !string.Equals(CurrentSettings.TranslationModel, snapshot.Model, StringComparison.Ordinal)
        || !LanguageCode.TargetLanguagesMatch(CurrentSettings.TargetLanguage, snapshot.TargetLanguage);

    private bool TtsSettingsDrifted(TtsExecutionSnapshot snapshot) =>
        CurrentSettings.TtsRuntime != snapshot.Plan.Runtime
        || CurrentSettings.TtsProfile != snapshot.Plan.Profile
        || !string.Equals(CurrentSettings.TtsProvider, snapshot.Plan.ProviderId, StringComparison.Ordinal)
        || !string.Equals(CurrentSettings.TtsVoice, snapshot.Voice, StringComparison.Ordinal)
        || CurrentSettings.DubTimingMode != snapshot.DefaultTimingMode
        || CurrentSettings.AmbianceMixDb != snapshot.AmbianceMixDb;

    private static bool DictionariesEqual<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? current,
        IReadOnlyDictionary<TKey, TValue>? captured)
        where TKey : notnull
    {
        current ??= EmptyReadOnlyDictionary<TKey, TValue>.Instance;
        captured ??= EmptyReadOnlyDictionary<TKey, TValue>.Instance;
        if (current.Count != captured.Count)
            return false;

        foreach (var (key, value) in captured)
        {
            if (!current.TryGetValue(key, out var currentValue)
                || !EqualityComparer<TValue>.Default.Equals(currentValue, value))
            {
                return false;
            }
        }

        return true;
    }

    private static class EmptyReadOnlyDictionary<TKey, TValue> where TKey : notnull
    {
        public static readonly IReadOnlyDictionary<TKey, TValue> Instance =
            new Dictionary<TKey, TValue>();
    }
}
