using System;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;
    private readonly AppLog _log;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-settings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "app-settings.json");
        _log = new AppLog(Path.Combine(_dir, "settings.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void LoadOrDefault_LegacyContainerizedRuntime_MigratesToGpuProfilesAndDockerBackend()
    {
        File.WriteAllText(
            _settingsPath,
            """
            {
              "TranscriptionProvider": "containerized",
              "TranscriptionRuntime": "Containerized",
              "TranslationProvider": "containerized",
              "TranslationRuntime": "Containerized",
              "TtsProvider": "containerized",
              "TtsRuntime": "Containerized",
              "ContainerizedServiceUrl": "http://localhost:8000",
              "AlwaysRunContainerAtAppStart": true
            }
            """);

        var service = new SettingsService(_settingsPath, _log);
        var settings = service.LoadOrDefault();

        Assert.Equal(ComputeProfile.Gpu, settings.TranscriptionProfile);
        Assert.Equal(ProviderNames.FasterWhisper, settings.TranscriptionProvider);
        Assert.Equal(ComputeProfile.Gpu, settings.TranslationProfile);
        Assert.Equal(ProviderNames.Nllb200, settings.TranslationProvider);
        Assert.Equal(ComputeProfile.Gpu, settings.TtsProfile);
        Assert.Equal(ProviderNames.Qwen, settings.TtsProvider);
        Assert.Equal(GpuHostBackend.DockerHost, settings.PreferredLocalGpuBackend);
        Assert.True(settings.AlwaysStartLocalGpuRuntimeAtAppStart);
        Assert.Equal("http://localhost:8000", settings.AdvancedGpuServiceUrl);
    }

    [Fact]
    public void LoadOrDefault_WhenProfilesMissing_InfersProfilesFromProviderCatalog()
    {
        File.WriteAllText(
            _settingsPath,
            """
            {
              "TranscriptionProvider": "gemini-transcription",
              "TranslationProvider": "nllb-200",
              "TtsProvider": "piper"
            }
            """);

        var service = new SettingsService(_settingsPath, _log);
        var settings = service.LoadOrDefault();

        Assert.Equal(ComputeProfile.Cloud, settings.TranscriptionProfile);
        Assert.Equal(ComputeProfile.Cpu, settings.TranslationProfile);
        Assert.Equal(ComputeProfile.Cpu, settings.TtsProfile);
    }

    [Fact]
    public void Save_DoesNotPersistLegacyRuntimeFields()
    {
        var service = new SettingsService(_settingsPath, _log);
        service.Save(new AppSettings
        {
            TranscriptionProfile = ComputeProfile.Gpu,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranslationProfile = ComputeProfile.Cpu,
            TranslationProvider = ProviderNames.Nllb200,
            TtsProfile = ComputeProfile.Cloud,
            TtsProvider = ProviderNames.EdgeTts,
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
        });

        var json = File.ReadAllText(_settingsPath);

        Assert.Contains("\"TranscriptionProfile\": \"Gpu\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TranscriptionRuntime\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TranslationRuntime\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TtsRuntime\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ContainerizedServiceUrl\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AlwaysRunContainerAtAppStart\"", json, StringComparison.Ordinal);
    }
}
