using System;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
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
        bool ttsProviderChanged = settings.TtsProfile != CurrentSettings.TtsProfile
            || settings.TtsProvider != CurrentSettings.TtsProvider
            || settings.TtsVoice != CurrentSettings.TtsVoice
            || settings.PiperModelDir != CurrentSettings.PiperModelDir
            || (settings.TtsProfile == ComputeProfile.Gpu
                && (settings.PreferredLocalGpuBackend != CurrentSettings.PreferredLocalGpuBackend
                    || !string.Equals(settings.EffectiveGpuServiceUrl, CurrentSettings.EffectiveGpuServiceUrl, StringComparison.Ordinal)));

        CurrentSettings = settings;

        if (transcriptionProviderChanged) _transcriptionService = null;
        if (translationProviderChanged) _translationService = null;
        if (ttsProviderChanged) _ttsService = null;

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
        _translationService = null;
        _ttsService = null;
    }

    /// <summary>
    /// Raises SettingsModified so subscribers (e.g. MainWindowViewModel) can persist changes.
    /// Call after any in-place mutation of CurrentSettings.
    /// </summary>
    public void NotifySettingsModified()
    {
        RefreshVideoEnhancementDiagnostics();
        SettingsModified?.Invoke();
    }
}
