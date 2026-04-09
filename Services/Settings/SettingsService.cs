using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Babel.Player.Models;

namespace Babel.Player.Services.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file.
/// Never throws — missing or corrupt files fall back to defaults silently.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    private readonly string _filePath;
    private readonly AppLog _log;

    public SettingsService(string filePath, AppLog log)
    {
        _filePath = filePath;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Returns saved settings, or a new <see cref="AppSettings"/> with defaults if the file
    /// is absent, empty, or unreadable.
    /// </summary>
    public AppSettings LoadOrDefault()
    {
        if (!File.Exists(_filePath))
        {
            var defaults = new AppSettings();
            defaults.NormalizeLegacyInferenceSettings();
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                var defaults = new AppSettings();
                defaults.NormalizeLegacyInferenceSettings();
                return defaults;
            }

            var file = JsonSerializer.Deserialize<AppSettingsFile>(json, SerializerOptions)
                ?? new AppSettingsFile();
            var settings = file.ToSettings();
            settings.NormalizeLegacyInferenceSettings();
            return settings;
        }
        catch (Exception ex)
        {
            _log.Warning($"Settings load failed ({ex.Message}). Using defaults.");
            var defaults = new AppSettings();
            defaults.NormalizeLegacyInferenceSettings();
            return defaults;
        }
    }

    /// <summary>Save settings. Failures are logged but non-fatal.</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            settings.NormalizeLegacyInferenceSettings();
            var file = AppSettingsFile.FromSettings(settings);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(file, SerializerOptions));
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save app settings.", ex);
        }
    }

    private sealed class AppSettingsFile
    {
        public string? TranscriptionProvider { get; set; }
        public ComputeProfile? TranscriptionProfile { get; set; }
        public InferenceRuntime? TranscriptionRuntime { get; set; }
        public string? TranscriptionModel { get; set; }
        public string? TranscriptionCpuComputeType { get; set; }
        public int? TranscriptionCpuThreads { get; set; }
        public int? TranscriptionNumWorkers { get; set; }
        public string? DiarizationProvider { get; set; }
        public int? DiarizationMinSpeakers { get; set; }
        public int? DiarizationMaxSpeakers { get; set; }
        public string? TranslationProvider { get; set; }
        public ComputeProfile? TranslationProfile { get; set; }
        public InferenceRuntime? TranslationRuntime { get; set; }
        public string? TranslationModel { get; set; }
        public string? TtsProvider { get; set; }
        public ComputeProfile? TtsProfile { get; set; }
        public InferenceRuntime? TtsRuntime { get; set; }
        public string? TtsVoice { get; set; }
        public string? TargetLanguage { get; set; }
        public string? PiperModelDir { get; set; }
        public GpuHostBackend? PreferredLocalGpuBackend { get; set; }
        public bool? AlwaysStartLocalGpuRuntimeAtAppStart { get; set; }
        public string? AdvancedGpuServiceUrl { get; set; }

        // Legacy compatibility inputs.
        public string? ContainerizedServiceUrl { get; set; }
        public bool? AlwaysRunContainerAtAppStart { get; set; }

        public string? VideoHwdec { get; set; }
        public string? VideoGpuApi { get; set; }
        public bool? VideoUseGpuNext { get; set; }
        public bool? VideoVsrEnabled { get; set; }
        public bool? VideoHdrEnabled { get; set; }
        public string? VideoToneMapping { get; set; }
        public string? VideoTargetPeak { get; set; }
        public bool? VideoHdrComputePeak { get; set; }
        public string? VideoExportEncoder { get; set; }
        public string? Theme { get; set; }
        public int? MaxRecentSessions { get; set; }
        public bool? AutoSaveEnabled { get; set; }

        public AppSettings ToSettings()
        {
            var settings = new AppSettings();

            settings.TranscriptionProvider = TranscriptionProvider ?? settings.TranscriptionProvider;
            settings.TranscriptionProfile = ResolveProfile(
                TranscriptionProfile,
                TranscriptionRuntime,
                settings.TranscriptionProvider,
                InferenceRuntimeCatalog.InferTranscriptionProfile);
            settings.TranscriptionModel = TranscriptionModel ?? settings.TranscriptionModel;
            settings.TranscriptionCpuComputeType = TranscriptionCpuComputeType ?? settings.TranscriptionCpuComputeType;
            settings.TranscriptionCpuThreads = TranscriptionCpuThreads ?? settings.TranscriptionCpuThreads;
            settings.TranscriptionNumWorkers = TranscriptionNumWorkers ?? settings.TranscriptionNumWorkers;

            settings.DiarizationProvider = DiarizationProvider ?? settings.DiarizationProvider;
            settings.DiarizationMinSpeakers = DiarizationMinSpeakers ?? settings.DiarizationMinSpeakers;
            settings.DiarizationMaxSpeakers = DiarizationMaxSpeakers ?? settings.DiarizationMaxSpeakers;

            settings.TranslationProvider = TranslationProvider ?? settings.TranslationProvider;
            settings.TranslationProfile = ResolveProfile(
                TranslationProfile,
                TranslationRuntime,
                settings.TranslationProvider,
                InferenceRuntimeCatalog.InferTranslationProfile);
            settings.TranslationModel = TranslationModel ?? settings.TranslationModel;

            settings.TtsProvider = TtsProvider ?? settings.TtsProvider;
            settings.TtsProfile = ResolveProfile(
                TtsProfile,
                TtsRuntime,
                settings.TtsProvider,
                InferenceRuntimeCatalog.InferTtsProfile);
            settings.TtsVoice = TtsVoice ?? settings.TtsVoice;

            settings.TargetLanguage = TargetLanguage ?? settings.TargetLanguage;
            settings.PiperModelDir = PiperModelDir ?? settings.PiperModelDir;

            settings.PreferredLocalGpuBackend = PreferredLocalGpuBackend
                ?? ResolveLegacyGpuBackend();
            settings.AlwaysStartLocalGpuRuntimeAtAppStart =
                AlwaysStartLocalGpuRuntimeAtAppStart
                ?? AlwaysRunContainerAtAppStart
                ?? settings.AlwaysStartLocalGpuRuntimeAtAppStart;
            settings.AdvancedGpuServiceUrl =
                AdvancedGpuServiceUrl
                ?? ContainerizedServiceUrl
                ?? settings.AdvancedGpuServiceUrl;

            settings.VideoHwdec = VideoHwdec ?? settings.VideoHwdec;
            settings.VideoGpuApi = VideoGpuApi ?? settings.VideoGpuApi;
            settings.VideoUseGpuNext = VideoUseGpuNext ?? settings.VideoUseGpuNext;
            settings.VideoVsrEnabled = VideoVsrEnabled ?? settings.VideoVsrEnabled;
            settings.VideoHdrEnabled = VideoHdrEnabled ?? settings.VideoHdrEnabled;
            settings.VideoToneMapping = VideoToneMapping ?? settings.VideoToneMapping;
            settings.VideoTargetPeak = VideoTargetPeak ?? settings.VideoTargetPeak;
            settings.VideoHdrComputePeak = VideoHdrComputePeak ?? settings.VideoHdrComputePeak;
            settings.VideoExportEncoder = VideoExportEncoder ?? settings.VideoExportEncoder;
            settings.Theme = Theme ?? settings.Theme;
            settings.MaxRecentSessions = MaxRecentSessions ?? settings.MaxRecentSessions;
            settings.AutoSaveEnabled = AutoSaveEnabled ?? settings.AutoSaveEnabled;

            return settings;
        }

        public static AppSettingsFile FromSettings(AppSettings settings) => new()
        {
            TranscriptionProvider = settings.TranscriptionProvider,
            TranscriptionProfile = settings.TranscriptionProfile,
            TranscriptionModel = settings.TranscriptionModel,
            TranscriptionCpuComputeType = settings.TranscriptionCpuComputeType,
            TranscriptionCpuThreads = settings.TranscriptionCpuThreads,
            TranscriptionNumWorkers = settings.TranscriptionNumWorkers,
            DiarizationProvider = settings.DiarizationProvider,
            DiarizationMinSpeakers = settings.DiarizationMinSpeakers,
            DiarizationMaxSpeakers = settings.DiarizationMaxSpeakers,
            TranslationProvider = settings.TranslationProvider,
            TranslationProfile = settings.TranslationProfile,
            TranslationModel = settings.TranslationModel,
            TtsProvider = settings.TtsProvider,
            TtsProfile = settings.TtsProfile,
            TtsVoice = settings.TtsVoice,
            TargetLanguage = settings.TargetLanguage,
            PiperModelDir = settings.PiperModelDir,
            PreferredLocalGpuBackend = settings.PreferredLocalGpuBackend,
            AlwaysStartLocalGpuRuntimeAtAppStart = settings.AlwaysStartLocalGpuRuntimeAtAppStart,
            AdvancedGpuServiceUrl = settings.AdvancedGpuServiceUrl,
            VideoHwdec = settings.VideoHwdec,
            VideoGpuApi = settings.VideoGpuApi,
            VideoUseGpuNext = settings.VideoUseGpuNext,
            VideoVsrEnabled = settings.VideoVsrEnabled,
            VideoHdrEnabled = settings.VideoHdrEnabled,
            VideoToneMapping = settings.VideoToneMapping,
            VideoTargetPeak = settings.VideoTargetPeak,
            VideoHdrComputePeak = settings.VideoHdrComputePeak,
            VideoExportEncoder = settings.VideoExportEncoder,
            Theme = settings.Theme,
            MaxRecentSessions = settings.MaxRecentSessions,
            AutoSaveEnabled = settings.AutoSaveEnabled,
        };

        private ComputeProfile ResolveProfile(
            ComputeProfile? profile,
            InferenceRuntime? legacyRuntime,
            string? providerId,
            Func<string?, ComputeProfile> inferProfile)
        {
            if (profile.HasValue)
                return profile.Value;

            if (legacyRuntime.HasValue)
                return InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(legacyRuntime.Value);

            return inferProfile(providerId);
        }

        private GpuHostBackend ResolveLegacyGpuBackend()
        {
            if (PreferredLocalGpuBackend.HasValue)
                return PreferredLocalGpuBackend.Value;

            if (TranscriptionRuntime == InferenceRuntime.Containerized
                || TranslationRuntime == InferenceRuntime.Containerized
                || TtsRuntime == InferenceRuntime.Containerized
                || AlwaysRunContainerAtAppStart == true
                || !string.IsNullOrWhiteSpace(ContainerizedServiceUrl))
            {
                return GpuHostBackend.DockerHost;
            }

            return GpuHostBackend.ManagedVenv;
        }
    }
}
