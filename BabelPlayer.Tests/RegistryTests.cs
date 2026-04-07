using System;
using System.IO;
using System.Linq;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

/// <summary>
/// Tests for TranscriptionRegistry, TranslationRegistry, and TtsRegistry —
/// GetAvailableProviders, CheckReadiness, and CreateProvider.
/// These tests only exercise the logic paths that do not require downloaded models
/// or live external services.
/// </summary>
public sealed class RegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;
    private readonly TranscriptionRegistry _transcriptionRegistry;
    private readonly TranslationRegistry _translationRegistry;
    private readonly TtsRegistry _ttsRegistry;
    private readonly DiarizationRegistry _diarizationRegistry;

    public RegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-registry-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _transcriptionRegistry = new TranscriptionRegistry(_log);
        _translationRegistry = new TranslationRegistry(_log);
        _ttsRegistry = new TtsRegistry(_log);
        _diarizationRegistry = new DiarizationRegistry(_log);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── TranscriptionRegistry ──────────────────────────────────────────────────

    [Fact]
    public void TranscriptionRegistry_GetAvailableProviders_ReturnsNonEmpty()
    {
        var providers = _transcriptionRegistry.GetAvailableProviders();
        Assert.NotEmpty(providers);
    }

    [Fact]
    public void TranscriptionRegistry_GetAvailableProviders_ContainsFasterWhisper()
    {
        var providers = _transcriptionRegistry.GetAvailableProviders();
        Assert.Contains(providers, p => p.Id == ProviderNames.FasterWhisper);
    }

    [Fact]
    public void TranscriptionRegistry_GetAvailableProviders_AllHaveNonEmptyId()
    {
        foreach (var p in _transcriptionRegistry.GetAvailableProviders())
            Assert.False(string.IsNullOrWhiteSpace(p.Id), $"Provider has empty Id: {p.DisplayName}");
    }

    [Fact]
    public void TranscriptionRegistry_CheckReadiness_UnknownProvider_ReturnsNotReady()
    {
        var readiness = _transcriptionRegistry.CheckReadiness("nonexistent-provider", "base", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("nonexistent-provider", readiness.BlockingReason);
    }

    [Fact]
    public void TranscriptionRegistry_CheckReadiness_OpenAiWhisperWithoutKey_ReturnsNotReady()
    {
        var readiness = _transcriptionRegistry.CheckReadiness(
            ProviderNames.OpenAiWhisperApi, "whisper-1", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("API key missing", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranscriptionRegistry_CheckReadiness_GoogleSttWithoutKey_ReturnsNotReady()
    {
        var readiness = _transcriptionRegistry.CheckReadiness(
            ProviderNames.GoogleStt, "default", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("API key missing", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranscriptionRegistry_CreateProvider_UnknownProvider_ThrowsPipelineProviderException()
    {
        Assert.Throws<PipelineProviderException>(() =>
            _transcriptionRegistry.CreateProvider("unknown-xyz", new AppSettings(), null));
    }

    [Fact]
    public void TranscriptionRegistry_CreateProvider_FasterWhisper_DoesNotThrow()
    {
        // Creating the provider itself should succeed — it just builds the object
        var provider = _transcriptionRegistry.CreateProvider(ProviderNames.FasterWhisper, new AppSettings(), null);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TranscriptionRegistry_CreateProvider_OpenAiWhisper_DoesNotThrow()
    {
        var provider = _transcriptionRegistry.CreateProvider(ProviderNames.OpenAiWhisperApi, new AppSettings(), null);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TranscriptionRegistry_CreateProvider_GoogleStt_DoesNotThrow()
    {
        var provider = _transcriptionRegistry.CreateProvider(ProviderNames.GoogleStt, new AppSettings(), null);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TranscriptionRegistry_AllUnimplementedProviders_HaveIsImplementedFalse()
    {
        foreach (var p in _transcriptionRegistry.GetAvailableProviders().Where(p => !p.IsImplemented))
        {
            // If not implemented, CheckReadiness should block
            var r = _transcriptionRegistry.CheckReadiness(p.Id, "base", new AppSettings(), null);
            Assert.False(r.IsReady, $"Provider '{p.Id}' is unimplemented but CheckReadiness returned ready.");
        }
    }

    // ── TranslationRegistry ────────────────────────────────────────────────────

    [Fact]
    public void TranslationRegistry_GetAvailableProviders_ReturnsNonEmpty()
    {
        var providers = _translationRegistry.GetAvailableProviders();
        Assert.NotEmpty(providers);
    }

    [Fact]
    public void TranslationRegistry_GetAvailableProviders_ContainsGoogleTranslateFree()
    {
        var providers = _translationRegistry.GetAvailableProviders();
        Assert.Contains(providers, p => p.Id == ProviderNames.GoogleTranslateFree);
    }

    [Fact]
    public void TranslationRegistry_GetAvailableProviders_ContainsCTranslate2()
    {
        var providers = _translationRegistry.GetAvailableProviders();
        Assert.Contains(providers, p => p.Id == ProviderNames.CTranslate2);
    }

    [Fact]
    public void TranslationRegistry_CheckReadiness_UnknownProvider_ReturnsNotReady()
    {
        var readiness = _translationRegistry.CheckReadiness("nonexistent-provider", "default", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("nonexistent-provider", readiness.BlockingReason);
    }

    [Fact]
    public void TranslationRegistry_CheckReadiness_DeepLWithoutKey_ReturnsNotReady()
    {
        var readiness = _translationRegistry.CheckReadiness(
            ProviderNames.Deepl, "default", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("API key missing", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranslationRegistry_CreateProvider_UnknownProvider_ThrowsPipelineProviderException()
    {
        Assert.Throws<PipelineProviderException>(() =>
            _translationRegistry.CreateProvider("unknown-xyz", new AppSettings(), null));
    }

    [Fact]
    public void TranslationRegistry_CreateProvider_GoogleTranslateFree_DoesNotThrow()
    {
        var provider = _translationRegistry.CreateProvider(ProviderNames.GoogleTranslateFree, new AppSettings(), null);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TranslationRegistry_CreateProvider_DeepL_DoesNotThrow()
    {
        var keyStore = new ApiKeyStore(new FileSystemCredentialProvider(Path.Combine(_dir, "api-keys.json")));
        keyStore.SetKey(CredentialKeys.Deepl, "test-deepl-key");


        var provider = _translationRegistry.CreateProvider(ProviderNames.Deepl, new AppSettings(), keyStore);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TranslationRegistry_CheckReadiness_OpenAiWithoutKey_ReturnsMissingKey()
    {
        var readiness = _translationRegistry.CheckReadiness(
            ProviderNames.OpenAi, "gpt-4o-mini",
            new AppSettings { TranslationProvider = ProviderNames.OpenAi, TranslationModel = "gpt-4o-mini", TranslationProfile = ComputeProfile.Cloud },
            null);

        Assert.False(readiness.IsReady);
        Assert.Contains("API key missing", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranslationRegistry_CreateProvider_OpenAi_DoesNotThrow()
    {
        var provider = _translationRegistry.CreateProvider(
            ProviderNames.OpenAi,
            new AppSettings { TranslationProvider = ProviderNames.OpenAi, TranslationModel = "gpt-4o-mini" },
            null);

        Assert.NotNull(provider);
    }

    [Fact]
    public void TranslationRegistry_CreateProvider_CTranslate2_DoesNotThrow()
    {
        var provider = _translationRegistry.CreateProvider(
            ProviderNames.CTranslate2,
            new AppSettings { TranslationProvider = ProviderNames.CTranslate2, TranslationModel = "nllb-200-distilled-600M" },
            null,
            ComputeProfile.Cpu);

        Assert.NotNull(provider);
    }

    [Fact]
    public void TranslationRegistry_AllUnimplementedProviders_HaveIsImplementedFalse()
    {
        foreach (var p in _translationRegistry.GetAvailableProviders().Where(p => !p.IsImplemented))
        {
            var r = _translationRegistry.CheckReadiness(p.Id, "default", new AppSettings(), null);
            Assert.False(r.IsReady, $"Provider '{p.Id}' is unimplemented but CheckReadiness returned ready.");
        }
    }

    // ── TtsRegistry ───────────────────────────────────────────────────────────

    [Fact]
    public void TtsRegistry_GetAvailableProviders_ReturnsNonEmpty()
    {
        var providers = _ttsRegistry.GetAvailableProviders();
        Assert.NotEmpty(providers);
    }

    [Fact]
    public void TtsRegistry_GetAvailableProviders_ContainsEdgeTts()
    {
        var providers = _ttsRegistry.GetAvailableProviders();
        Assert.Contains(providers, p => p.Id == ProviderNames.EdgeTts);
    }

    [Fact]
    public void TtsRegistry_GetAvailableProviders_IncludesXttsContainer()
    {
        var providers = _ttsRegistry.GetAvailableProviders();
        Assert.Contains(providers, p => p.Id == ProviderNames.XttsContainer);
    }

    [Fact]
    public void TtsRegistry_CheckReadiness_UnknownProvider_ReturnsNotReady()
    {
        var readiness = _ttsRegistry.CheckReadiness("nonexistent-provider", "voice", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("nonexistent-provider", readiness.BlockingReason);
    }

    [Fact]
    public void TtsRegistry_CheckReadiness_ElevenLabsWithoutKey_ReturnsNotReady()
    {
        // ElevenLabs is implemented but RequiresApiKey — missing key blocks readiness.
        var readiness = _ttsRegistry.CheckReadiness(
            ProviderNames.ElevenLabs, "eleven_multilingual_v2", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.NotNull(readiness.BlockingReason);
    }

    [Fact]
    public void TtsRegistry_CheckReadiness_OpenAiTtsWithoutKey_ReturnsNotReady()
    {
        var readiness = _ttsRegistry.CheckReadiness(
            ProviderNames.OpenAiTts, "tts-1", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("API key missing", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TtsRegistry_CreateProvider_UnknownProvider_ThrowsPipelineProviderException()
    {
        Assert.Throws<PipelineProviderException>(() =>
            _ttsRegistry.CreateProvider("unknown-xyz", new AppSettings(), null));
    }

    [Fact]
    public void TtsRegistry_CreateProvider_EdgeTts_DoesNotThrow()
    {
        var provider = _ttsRegistry.CreateProvider(ProviderNames.EdgeTts, new AppSettings(), null);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TtsRegistry_CreateProvider_XttsContainer_DoesNotThrow_WhenRuntimeIsContainerized()
    {
        var settings = new AppSettings { TtsRuntime = InferenceRuntime.Containerized, TtsProvider = ProviderNames.XttsContainer };
        var provider = _ttsRegistry.CreateProvider(ProviderNames.XttsContainer, settings, null, ComputeProfile.Gpu);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TtsRegistry_CreateProvider_ElevenLabs_DoesNotThrow()
    {
        // Key is empty — provider is created but CheckReadiness will report missing key.
        var provider = _ttsRegistry.CreateProvider(ProviderNames.ElevenLabs, new AppSettings(), null);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TtsRegistry_CreateProvider_OpenAiTts_DoesNotThrow()
    {
        var provider = _ttsRegistry.CreateProvider(ProviderNames.OpenAiTts, new AppSettings(), null);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TtsRegistry_AllUnimplementedProviders_HaveIsImplementedFalse()
    {
        foreach (var p in _ttsRegistry.GetAvailableProviders().Where(p => !p.IsImplemented))
        {
            var r = _ttsRegistry.CheckReadiness(p.Id, "voice", new AppSettings(), null);
            Assert.False(r.IsReady, $"Provider '{p.Id}' is unimplemented but CheckReadiness returned ready.");
        }
    }

    [Fact]
    public void TtsRegistry_EdgeTtsVoices_IsNonEmpty()
    {
        Assert.NotEmpty(TtsRegistry.EdgeTtsVoices);
    }

    [Fact]
    public void TtsRegistry_PiperVoices_IsNonEmpty()
    {
        Assert.NotEmpty(TtsRegistry.PiperVoices);
    }

    [Fact]
    public void TtsRegistry_GetAvailableProviders_GpuListContainsQwen()
    {
        var providers = _ttsRegistry.GetAvailableProviders(ComputeProfile.Gpu);
        Assert.Contains(providers, p => p.Id == ProviderNames.Qwen);
    }

    [Fact]
    public void TtsRegistry_GetAvailableProviders_QwenIsImplemented()
    {
        var providers = _ttsRegistry.GetAvailableProviders(ComputeProfile.Gpu);
        var qwen = providers.First(p => p.Id == ProviderNames.Qwen);
        Assert.True(qwen.IsImplemented);
    }

    [Fact]
    public void TtsRegistry_GetAvailableModels_QwenGpuProfile_ReturnsBothBaseModels()
    {
        var settings = new AppSettings { TtsProfile = ComputeProfile.Gpu, TtsProvider = ProviderNames.Qwen };
        var models = _ttsRegistry.GetAvailableModels(ProviderNames.Qwen, ComputeProfile.Gpu, settings);

        Assert.Contains("Qwen/Qwen3-TTS-12Hz-1.7B-Base", models);
        Assert.Contains("Qwen/Qwen3-TTS-12Hz-0.6B-Base", models);
    }

    [Fact]
    public void TtsRegistry_CreateProvider_Qwen_DoesNotThrow_WhenRuntimeIsContainerized()
    {
        var settings = new AppSettings { TtsRuntime = InferenceRuntime.Containerized, TtsProvider = ProviderNames.Qwen };
        var provider = _ttsRegistry.CreateProvider(ProviderNames.Qwen, settings, null, ComputeProfile.Gpu);
        Assert.NotNull(provider);
        Assert.IsType<QwenContainerTtsProvider>(provider);
    }

    [Fact]
    public void TtsRegistry_QwenModels_ContainsBothBaseModels()
    {
        Assert.Contains("Qwen/Qwen3-TTS-12Hz-1.7B-Base", TtsRegistry.QwenModels);
        Assert.Contains("Qwen/Qwen3-TTS-12Hz-0.6B-Base", TtsRegistry.QwenModels);
    }

    [Fact]
    public void InferenceRuntimeCatalog_InferTtsProfile_QwenReturnsGpu()
    {
        Assert.Equal(ComputeProfile.Gpu, InferenceRuntimeCatalog.InferTtsProfile(ProviderNames.Qwen));
    }

    [Fact]
    public void InferenceRuntimeCatalog_IsKnownTtsProvider_QwenReturnsTrue()
    {
        Assert.True(InferenceRuntimeCatalog.IsKnownTtsProvider(ProviderNames.Qwen));
    }

    [Fact]
    public void InferenceRuntimeCatalog_NormalizeTtsProvider_QwenOnGpu_PreservesQwenId()
    {
        var normalized = InferenceRuntimeCatalog.NormalizeTtsProvider(ComputeProfile.Gpu, ProviderNames.Qwen);
        Assert.Equal(ProviderNames.Qwen, normalized);
    }

    [Fact]
    public void InferenceRuntimeCatalog_NormalizeTtsProvider_XttsOnGpu_PreservesXttsId()
    {
        var normalized = InferenceRuntimeCatalog.NormalizeTtsProvider(ComputeProfile.Gpu, ProviderNames.XttsContainer);
        Assert.Equal(ProviderNames.XttsContainer, normalized);
    }

    [Fact]
    public void AllRegistries_ContainerizedService_CheckReadiness_RequiresConfiguredUrl()
    {
        var settingsWithEmptyUrl = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            AdvancedGpuServiceUrl = ""
        };

        var transcription = _transcriptionRegistry.CheckReadiness(
            ProviderNames.FasterWhisper, "base", settingsWithEmptyUrl, null, ComputeProfile.Gpu);
        var translation = _translationRegistry.CheckReadiness(
            ProviderNames.Nllb200, "nllb-200-distilled-1.3B", settingsWithEmptyUrl, null, ComputeProfile.Gpu);
        var tts = _ttsRegistry.CheckReadiness(
            ProviderNames.XttsContainer, "xtts-v2", settingsWithEmptyUrl, null, ComputeProfile.Gpu);

        Assert.False(transcription.IsReady);
        Assert.False(translation.IsReady);
        Assert.False(tts.IsReady);
    }

    [Fact]
    public void AllRegistries_ContainerizedService_CheckReadiness_NotReadyWhenServiceUnavailable()
    {
        var settingsWithUrl = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            AdvancedGpuServiceUrl = "http://127.0.0.1:1"
        };

        var transcription = _transcriptionRegistry.CheckReadiness(
            ProviderNames.FasterWhisper, "base", settingsWithUrl, null, ComputeProfile.Gpu);
        var translation = _translationRegistry.CheckReadiness(
            ProviderNames.Nllb200, "nllb-200-distilled-1.3B", settingsWithUrl, null, ComputeProfile.Gpu);
        var tts = _ttsRegistry.CheckReadiness(
            ProviderNames.XttsContainer, "xtts-v2", settingsWithUrl, null, ComputeProfile.Gpu);

        Assert.False(transcription.IsReady);
        Assert.False(translation.IsReady);
        Assert.False(tts.IsReady);
    }

    [Fact]
    public void TranscriptionRegistry_GetAvailableProviders_GpuFiltersToHostedProviders()
    {
        var providers = _transcriptionRegistry.GetAvailableProviders(ComputeProfile.Gpu);

        Assert.Single(providers);
        Assert.Equal(ProviderNames.FasterWhisper, providers[0].Id);
    }

    [Fact]
    public void TranslationRegistry_GetAvailableProviders_CpuFiltersToLocalProviders()
    {
        var providers = _translationRegistry.GetAvailableProviders(ComputeProfile.Cpu);

        Assert.DoesNotContain(providers, provider => provider.Id == ProviderNames.Nllb200);
        Assert.Contains(providers, provider => provider.Id == ProviderNames.CTranslate2);
    }

    [Fact]
    public void TtsRegistry_GetAvailableProviders_CloudExcludesLocalOnlyProviders()
    {
        var providers = _ttsRegistry.GetAvailableProviders(ComputeProfile.Cloud);

        Assert.DoesNotContain(providers, provider => provider.Id == ProviderNames.Piper);
        Assert.DoesNotContain(providers, provider => provider.Id == ProviderNames.XttsContainer);
        Assert.DoesNotContain(providers, provider => provider.Id == ProviderNames.Qwen);
        Assert.DoesNotContain(providers, provider => provider.Id == ProviderNames.GoogleCloudTts);
        Assert.Contains(providers, provider => provider.Id == ProviderNames.EdgeTts);
    }

    [Fact]
    public void TtsRegistry_GetAvailableProviders_GpuShowsXttsContainerAndQwen()
    {
        var providers = _ttsRegistry.GetAvailableProviders(ComputeProfile.Gpu);

        Assert.Equal(2, providers.Count);
        Assert.Contains(providers, p => p.Id == ProviderNames.XttsContainer);
        Assert.Contains(providers, p => p.Id == ProviderNames.Qwen);
    }

    [Fact]
    public void TranslationRegistry_GetAvailableModels_NllbCpuProfile_ReturnsOnlyCpuModel()
    {
        var settings = new AppSettings
        {
            TranslationProfile = ComputeProfile.Cpu,
            TranslationProvider = ProviderNames.Nllb200
        };

        var models = _translationRegistry.GetAvailableModels(ProviderNames.Nllb200, ComputeProfile.Cpu, settings);

        Assert.Equal(["nllb-200-distilled-600M"], models);
        Assert.DoesNotContain("nllb-200-distilled-1.3B", models);
        Assert.DoesNotContain("nllb-200-1.3B", models);
    }

    [Fact]
    public void TranslationRegistry_GetAvailableModels_NllbGpuProfile_ReturnsOnlyGpuModels()
    {
        var settings = new AppSettings
        {
            TranslationProfile = ComputeProfile.Gpu,
            TranslationProvider = ProviderNames.Nllb200
        };

        var models = _translationRegistry.GetAvailableModels(ProviderNames.Nllb200, ComputeProfile.Gpu, settings);

        Assert.Contains("nllb-200-distilled-1.3B", models);
        Assert.Contains("nllb-200-1.3B", models);
        Assert.DoesNotContain("nllb-200-distilled-600M", models);
    }

    [Fact]
    public void TranslationRegistry_GetAvailableModels_CTranslate2CpuProfile_ReturnsOnlyLightweightModel()
    {
        var settings = new AppSettings
        {
            TranslationProfile = ComputeProfile.Cpu,
            TranslationProvider = ProviderNames.CTranslate2
        };

        var models = _translationRegistry.GetAvailableModels(ProviderNames.CTranslate2, ComputeProfile.Cpu, settings);

        Assert.Equal(["nllb-200-distilled-600M"], models);
    }

    // ── DiarizationRegistry ───────────────────────────────────────────────────────

    [Fact]
    public void DiarizationRegistry_GetAvailableProviders_ReturnsNonEmpty()
    {
        var providers = _diarizationRegistry.GetAvailableProviders();
        Assert.NotEmpty(providers);
    }

    [Fact]
    public void DiarizationRegistry_GetAvailableProviders_ContainsPyannoteLocal()
    {
        var providers = _diarizationRegistry.GetAvailableProviders();
        Assert.Contains(providers, p => p.Id == ProviderNames.PyannoteLocal);
    }

    [Fact]
    public void DiarizationRegistry_PyannoteLocal_IsImplemented()
    {
        var providers = _diarizationRegistry.GetAvailableProviders();
        var pyannote = Assert.Single(providers, p => p.Id == ProviderNames.PyannoteLocal);
        Assert.True(pyannote.IsImplemented);
    }

    [Fact]
    public void DiarizationRegistry_PyannoteLocal_CheckReadiness_WithNoToken_ReturnsNotReady()
    {
        var keyStore = new ApiKeyStore(new FileSystemCredentialProvider(Path.Combine(_dir, "api-keys.json")));
        var readiness = _diarizationRegistry.CheckReadiness(ProviderNames.PyannoteLocal, new AppSettings(), keyStore);

        Assert.False(readiness.IsReady);
        Assert.Contains("HuggingFace token is required", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiarizationRegistry_PyannoteLocal_CheckReadiness_WithValidToken_DoesNotBlockOnToken()
    {
        var keyStore = new ApiKeyStore(new FileSystemCredentialProvider(Path.Combine(_dir, "api-keys.json")));
        keyStore.SetKey(CredentialKeys.HuggingFace, "hf_test_token");


        // Note: CheckReadiness will still run the pyannote.audio import probe,
        // which may return NotReady in CI if pyannote isn't installed —
        // but the blocking reason will NOT mention HuggingFace.
        var readiness = _diarizationRegistry.CheckReadiness(
            ProviderNames.PyannoteLocal, new AppSettings(), keyStore);

        if (!readiness.IsReady)
        {
            Assert.DoesNotContain("HuggingFace", readiness.BlockingReason ?? "",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DiarizationRegistry_PyannoteLocal_CreateProvider_DoesNotThrow()
    {
        var keyStore = new ApiKeyStore(new FileSystemCredentialProvider(Path.Combine(_dir, "empty-keys.json")));
        var provider = _diarizationRegistry.CreateProvider(ProviderNames.PyannoteLocal, new AppSettings(), keyStore);

        Assert.NotNull(provider);
    }

    [Fact]
    public void DiarizationRegistry_CheckReadiness_UnknownProvider_ReturnsNotReady()
    {
        var readiness = _diarizationRegistry.CheckReadiness("nonexistent-diarizer", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("nonexistent-diarizer", readiness.BlockingReason);
    }

    [Fact]
    public void DiarizationRegistry_CreateProvider_UnknownProvider_ThrowsPipelineProviderException()
    {
        Assert.Throws<PipelineProviderException>(
            () => _diarizationRegistry.CreateProvider("nonexistent-diarizer", new AppSettings(), null));
    }
}
