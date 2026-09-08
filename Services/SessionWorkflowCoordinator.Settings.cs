using System;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    private bool ApplyPipelineSelectionSettings(PipelineSettingsSelection selection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TranscriptionProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TranscriptionModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TranslationProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TranslationModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TtsProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TtsVoice);

        var transcriptionProviderChanged =
            CurrentSettings.TranscriptionProfile != selection.TranscriptionRuntime ||
            !string.Equals(CurrentSettings.TranscriptionProvider, selection.TranscriptionProvider, StringComparison.Ordinal) ||
            !string.Equals(CurrentSettings.TranscriptionModel, selection.TranscriptionModel, StringComparison.Ordinal) ||
            !SessionSnapshotSemantics.TranscriptionLanguageHintsMatch(
                CurrentSettings.TranscriptionLanguageHint,
                selection.TranscriptionLanguageHint);
        var translationProviderChanged =
            CurrentSettings.TranslationProfile != selection.TranslationRuntime ||
            !string.Equals(CurrentSettings.TranslationProvider, selection.TranslationProvider, StringComparison.Ordinal) ||
            !string.Equals(CurrentSettings.TranslationModel, selection.TranslationModel, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(selection.TargetLanguage) &&
             !LanguageCode.TargetLanguagesMatch(CurrentSettings.TargetLanguage, selection.TargetLanguage));
        var ttsProviderChanged =
            CurrentSettings.TtsProfile != selection.TtsRuntime ||
            !string.Equals(CurrentSettings.TtsProvider, selection.TtsProvider, StringComparison.Ordinal) ||
            !string.Equals(CurrentSettings.TtsVoice, selection.TtsVoice, StringComparison.Ordinal);

        var settingsChanged = transcriptionProviderChanged || translationProviderChanged || ttsProviderChanged;
        if (!settingsChanged)
            return false;

        CurrentSettings.TranscriptionProfile = selection.TranscriptionRuntime;
        CurrentSettings.TranscriptionProvider = selection.TranscriptionProvider;
        CurrentSettings.TranscriptionModel = selection.TranscriptionModel;
        CurrentSettings.TranslationProfile = selection.TranslationRuntime;
        CurrentSettings.TranslationProvider = selection.TranslationProvider;
        CurrentSettings.TranslationModel = selection.TranslationModel;
        CurrentSettings.TtsProfile = selection.TtsRuntime;
        CurrentSettings.TtsProvider = selection.TtsProvider;
        CurrentSettings.TtsVoice = selection.TtsVoice;
        if (!string.IsNullOrWhiteSpace(selection.TargetLanguage))
        {
            CurrentSettings.TargetLanguage = LanguageCode.NormalizeForPersistence(selection.TargetLanguage)
                ?? selection.TargetLanguage.Trim();
        }

        CurrentSettings.TranscriptionLanguageHint =
            SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(selection.TranscriptionLanguageHint);

        if (transcriptionProviderChanged)
            _transcriptionService = null;
        if (translationProviderChanged)
            RetireTranslationProviderCache("pipeline selection changed translation provider");
        if (ttsProviderChanged)
        {
            RetireTtsProviderCache("pipeline selection changed tts provider");
        }

        // Pipeline selections do not change the vocal-separation service endpoint, so keep
        // the cached provider until UpdateSettings changes the backing container URL.
        MarkSettingsInputsChanged("pipeline selection updated");
        return true;
    }

    public void UpdateSettings(AppSettings settings)
    {
        settings.NormalizeLegacyInferenceSettings();

        bool transcriptionProviderChanged = settings.TranscriptionProfile != CurrentSettings.TranscriptionProfile
            || settings.TranscriptionProvider != CurrentSettings.TranscriptionProvider
            || settings.TranscriptionModel != CurrentSettings.TranscriptionModel
            || (settings.TranscriptionProfile == ComputeProfile.Gpu
                && (settings.PreferredLocalGpuBackend != CurrentSettings.PreferredLocalGpuBackend
                    || !string.Equals(settings.EffectiveGpuServiceUrl, CurrentSettings.EffectiveGpuServiceUrl, StringComparison.Ordinal)));
        bool translationProviderChanged = settings.TranslationProfile != CurrentSettings.TranslationProfile
            || settings.TranslationProvider != CurrentSettings.TranslationProvider
            || settings.TranslationModel != CurrentSettings.TranslationModel
            || (settings.TranslationProfile == ComputeProfile.Gpu
                && (settings.PreferredLocalGpuBackend != CurrentSettings.PreferredLocalGpuBackend
                    || !string.Equals(settings.EffectiveGpuServiceUrl, CurrentSettings.EffectiveGpuServiceUrl, StringComparison.Ordinal)));
        bool translationExecutionChanged = translationProviderChanged
            || !LanguageCode.TargetLanguagesMatch(settings.TargetLanguage, CurrentSettings.TargetLanguage);
        bool ttsProviderChanged = settings.TtsProfile != CurrentSettings.TtsProfile
            || settings.TtsProvider != CurrentSettings.TtsProvider
            || settings.TtsVoice != CurrentSettings.TtsVoice
            || settings.PiperModelDir != CurrentSettings.PiperModelDir
            || settings.ChatterboxVoiceCloneConsent != CurrentSettings.ChatterboxVoiceCloneConsent
            || (settings.TtsProfile == ComputeProfile.Gpu
                && (settings.PreferredLocalGpuBackend != CurrentSettings.PreferredLocalGpuBackend
                    || !string.Equals(settings.EffectiveGpuServiceUrl, CurrentSettings.EffectiveGpuServiceUrl, StringComparison.Ordinal)));
        bool ttsExecutionChanged = ttsProviderChanged
            || settings.DubTimingMode != CurrentSettings.DubTimingMode
            || settings.AmbianceMixDb != CurrentSettings.AmbianceMixDb;
        bool vocalSeparationProviderChanged = !string.Equals(
            settings.EffectiveContainerizedServiceUrl,
            CurrentSettings.EffectiveContainerizedServiceUrl,
            StringComparison.OrdinalIgnoreCase);

        CurrentSettings = settings;

        if (transcriptionProviderChanged) _transcriptionService = null;
        if (translationProviderChanged) RetireTranslationProviderCache("settings updated translation provider");
        if (ttsProviderChanged) RetireTtsProviderCache("settings updated tts provider");
        if (vocalSeparationProviderChanged)
        {
            (_vocalSeparationProvider as IDisposable)?.Dispose();
            _vocalSeparationProvider = null;
        }

        if (transcriptionProviderChanged || translationExecutionChanged || ttsExecutionChanged)
            MarkSettingsInputsChanged("settings object replaced");

        RefreshVideoEnhancementDiagnostics();
    }

    public MediaReloadRequest? ConsumePendingMediaReloadRequest()
    {
        var request = PendingMediaReloadRequest;
        PendingMediaReloadRequest = null;
        return request;
    }

    /// <summary>
    /// Invalidates all cached provider service instances, forcing them to be recreated
    /// on the next pipeline execution with fresh CurrentSettings. Called explicitly
    /// when user clicks Clear or when a complete reset is needed.
    /// </summary>
    public void InvalidateAllProviderCaches()
    {
        _transcriptionService = null;
        RetireTranslationProviderCache("all provider caches invalidated");
        RetireTtsProviderCache("all provider caches invalidated");
        (_vocalSeparationProvider as IDisposable)?.Dispose();
        _vocalSeparationProvider = null;
    }

    /// <summary>
    /// Raises SettingsModified so subscribers (e.g. MainWindowViewModel) can persist changes.
    /// Call after any in-place mutation of CurrentSettings.
    /// </summary>
    public void NotifySettingsModified()
    {
        RefreshVideoEnhancementDiagnostics();
        EmitReadinessSignal(
            ReadinessSignalKind.SettingsChanged,
            summary: "Settings updated.",
            source: nameof(NotifySettingsModified),
            forceRefresh: true);
        SettingsModified?.Invoke();
    }

    public void RequestReadinessRefresh(string reason = "Manual refresh")
    {
        EmitReadinessSignal(
            ReadinessSignalKind.DiagnosticsRefreshRequested,
            summary: reason,
            source: nameof(RequestReadinessRefresh),
            forceRefresh: true);
    }
}
