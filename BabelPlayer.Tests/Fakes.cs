using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class FakeTranscriptionRegistry : ITranscriptionRegistry
{
    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [new("fake-transcription", "Fake Transcription", false, null, ["default"])];

    public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
        ["default"];

    public ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
        new FakeTranscriptionProvider();

    public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
        new(true, "Ready");

    public Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
        Task.FromResult(true);
}

public sealed class FakeTranscriptionProvider : ITranscriptionProvider
{
    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default)
    {
        var segments = new List<TranscriptSegment>
        {
            new(0.0, 2.0, "Hello world."),
            new(2.0, 4.0, "This is a test transcription.")
        };

        var result = new TranscriptionResult(true, segments, "en", 1.0, null);

        var json = JsonSerializer.Serialize(new
        {
            success = true,
            segments = segments.Select(s => new
            {
                id = $"segment_{s.StartSeconds:G}",
                startSeconds = s.StartSeconds,
                endSeconds = s.EndSeconds,
                text = s.Text,
                speakerId = s.SpeakerId
            }),
            language = "en",
            languageProbability = 1.0,
            errorMessage = (string?)null
        });

        await File.WriteAllTextAsync(request.OutputJsonPath, json, ct);
        return result;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) => new(true, "Ready");
    public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) => Task.FromResult(true);
}

public sealed class FakeTranslationRegistry : ITranslationRegistry
{
    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [new("fake-translation", "Fake Translation", false, null, ["default"])];

    public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
        ["default"];

    public ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
        new FakeTranslationProvider();

    public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
        new(true, "Ready");

    public Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
        Task.FromResult(true);
}

public sealed class FakeTranslationProvider : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
    {
        var resultSegments = new List<TranslatedSegment>
        {
            new(0.0, 2.0, "Hello world.", "[Translated: Hello world.]"),
            new(2.0, 4.0, "This is a test transcription.", "[Translated: This is a test transcription.]")
        };

        var result = new TranslationResult(true, resultSegments, request.SourceLanguage, request.TargetLanguage, null);

        var json = JsonSerializer.Serialize(new
        {
            success = true,
            segments = resultSegments.Select(s => new
            {
                id = $"segment_{s.StartSeconds:G}",
                startSeconds = s.StartSeconds,
                endSeconds = s.EndSeconds,
                text = s.Text,
                translatedText = s.TranslatedText,
                speakerId = s.SpeakerId
            }),
            sourceLanguage = request.SourceLanguage,
            targetLanguage = request.TargetLanguage,
            errorMessage = (string?)null
        });

        await File.WriteAllTextAsync(request.OutputJsonPath, json, ct);
        return result;
    }

    public async Task<TranslationResult> TranslateSingleSegmentAsync(SingleSegmentTranslationRequest request, CancellationToken ct = default)
    {
        var resultSegments = new List<TranslatedSegment>
        {
            new(0.0, 2.0, "Hello world.", "[Translated: Hello world.]")
        };

        var result = new TranslationResult(true, resultSegments, request.SourceLanguage, request.TargetLanguage, null);
        
        var json = JsonSerializer.Serialize(new
        {
            success = true,
            segments = resultSegments.Select(s => new
            {
                id = $"segment_{s.StartSeconds:G}",
                startSeconds = s.StartSeconds,
                endSeconds = s.EndSeconds,
                text = s.Text,
                translatedText = s.TranslatedText,
                speakerId = s.SpeakerId
            }),
            sourceLanguage = request.SourceLanguage,
            targetLanguage = request.TargetLanguage,
            errorMessage = (string?)null
        });

        await File.WriteAllTextAsync(request.OutputJsonPath, json, ct);
        return result;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) => new(true, "Ready");
    public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) => Task.FromResult(true);
}

public sealed class FakeTtsRegistry : ITtsRegistry
{
    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [new("fake-tts", "Fake TTS", false, null, ["default"])];

    public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
        ["default"];

    public ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
        new FakeTtsProvider();

    public ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
        new(true, "Ready");

    public Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
        Task.FromResult(true);
}

public sealed class FakeTtsProvider : ITtsProvider
{
    public async Task<TtsResult> GenerateTtsAsync(TtsRequest request, CancellationToken cancellationToken = default)
    {
        await File.WriteAllBytesAsync(request.OutputAudioPath, [0x00, 0x01, 0x02], cancellationToken);
        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, 3, null);
    }

    public async Task<TtsResult> GenerateSegmentTtsAsync(SingleSegmentTtsRequest request, CancellationToken cancellationToken = default)
    {
        await File.WriteAllBytesAsync(request.OutputAudioPath, [0x00, 0x01, 0x02], cancellationToken);
        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, 3, null);
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) => new(true, "Ready");
    public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) => Task.FromResult(true);
}

public sealed class FakeAudioProcessingService : IAudioProcessingService
{
    public async Task CombineAudioSegmentsAsync(IReadOnlyList<string> segmentAudioPaths, string outputAudioPath, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(outputAudioPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(outputAudioPath, [0xAA, 0xBB], cancellationToken);
    }

    public async Task ExtractAudioClipAsync(string inputPath, string outputPath, double startTimeSeconds, double durationSeconds, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(outputPath, [0xCC, 0xDD], cancellationToken);
    }
}
