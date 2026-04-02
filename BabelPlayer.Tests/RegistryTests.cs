using System;
using System.IO;
using System.Linq;
using Babel.Player.Models;
using Babel.Player.Services;
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

    public RegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-registry-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _transcriptionRegistry = new TranscriptionRegistry(_log);
        _translationRegistry = new TranslationRegistry(_log);
        _ttsRegistry = new TtsRegistry(_log);
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
    public void TranscriptionRegistry_CheckReadiness_UnimplementedProvider_ReturnsNotReady()
    {
        // OpenAI Whisper API is marked IsImplemented=false
        var readiness = _transcriptionRegistry.CheckReadiness(
            ProviderNames.OpenAiWhisperApi, "whisper-1", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.NotNull(readiness.BlockingReason);
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
    public void TranslationRegistry_CheckReadiness_UnknownProvider_ReturnsNotReady()
    {
        var readiness = _translationRegistry.CheckReadiness("nonexistent-provider", "default", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("nonexistent-provider", readiness.BlockingReason);
    }

    [Fact]
    public void TranslationRegistry_CheckReadiness_UnimplementedProvider_ReturnsNotReady()
    {
        // DeepL is marked IsImplemented=false
        var readiness = _translationRegistry.CheckReadiness(
            ProviderNames.Deepl, "default", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.NotNull(readiness.BlockingReason);
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
    public void TranslationRegistry_CheckReadiness_OpenAiWithoutKey_ReturnsMissingKey()
    {
        var readiness = _translationRegistry.CheckReadiness(
            ProviderNames.OpenAi, "gpt-4o-mini", new AppSettings { TranslationProvider = ProviderNames.OpenAi, TranslationModel = "gpt-4o-mini" }, null);

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
    public void TtsRegistry_CheckReadiness_UnknownProvider_ReturnsNotReady()
    {
        var readiness = _ttsRegistry.CheckReadiness("nonexistent-provider", "voice", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.Contains("nonexistent-provider", readiness.BlockingReason);
    }

    [Fact]
    public void TtsRegistry_CheckReadiness_UnimplementedProvider_ReturnsNotReady()
    {
        // ElevenLabs is marked IsImplemented=false
        var readiness = _ttsRegistry.CheckReadiness(
            ProviderNames.ElevenLabs, "eleven_multilingual_v2", new AppSettings(), null);
        Assert.False(readiness.IsReady);
        Assert.NotNull(readiness.BlockingReason);
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

    // ── Cross-registry checks ─────────────────────────────────────────────────

    [Fact]
    public void AllRegistries_ContainerizedService_CheckReadiness_RequiresConfiguredUrl()
    {
        var settingsWithEmptyUrl = new AppSettings { ContainerizedServiceUrl = "" };

        var transcription = _transcriptionRegistry.CheckReadiness(
            ProviderNames.ContainerizedService, "base", settingsWithEmptyUrl, null);
        var translation = _translationRegistry.CheckReadiness(
            ProviderNames.ContainerizedService, "default", settingsWithEmptyUrl, null);
        var tts = _ttsRegistry.CheckReadiness(
            ProviderNames.ContainerizedService, "en-US-AriaNeural", settingsWithEmptyUrl, null);

        Assert.False(transcription.IsReady);
        Assert.False(translation.IsReady);
        Assert.False(tts.IsReady);
    }

    [Fact]
    public void AllRegistries_ContainerizedService_CheckReadiness_NotReadyWhenServiceUnavailable()
    {
        var settingsWithUrl = new AppSettings { ContainerizedServiceUrl = "http://127.0.0.1:1" };

        var transcription = _transcriptionRegistry.CheckReadiness(
            ProviderNames.ContainerizedService, "base", settingsWithUrl, null);
        var translation = _translationRegistry.CheckReadiness(
            ProviderNames.ContainerizedService, "default", settingsWithUrl, null);
        var tts = _ttsRegistry.CheckReadiness(
            ProviderNames.ContainerizedService, "en-US-AriaNeural", settingsWithUrl, null);

        Assert.False(transcription.IsReady);
        Assert.False(translation.IsReady);
        Assert.False(tts.IsReady);
    }
}
