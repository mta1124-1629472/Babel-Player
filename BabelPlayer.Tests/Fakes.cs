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
        var artifact = new TranscriptArtifact
        {
            SchemaVersion = ArtifactJson.CurrentSchemaVersion,
            Language = "en",
            LanguageProbability = 1.0,
            Segments = segments.Select(s => new TranscriptSegmentArtifact
            {
                Start = s.StartSeconds,
                End = s.EndSeconds,
                Text = s.Text,
                SpeakerId = s.SpeakerId,
            }).ToList(),
        };

        await File.WriteAllTextAsync(request.OutputJsonPath, ArtifactJson.SerializeTranscript(artifact), ct);
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

        var artifact = new TranslationArtifact
        {
            SchemaVersion = ArtifactJson.CurrentSchemaVersion,
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = request.TargetLanguage,
            Segments = resultSegments.Select(s => new TranslationSegmentArtifact
            {
                Id = SessionWorkflowCoordinator.SegmentId(s.StartSeconds),
                Start = s.StartSeconds,
                End = s.EndSeconds,
                Text = s.Text,
                TranslatedText = s.TranslatedText,
                SpeakerId = s.SpeakerId,
            }).ToList(),
        };

        await File.WriteAllTextAsync(request.OutputJsonPath, ArtifactJson.SerializeTranslation(artifact), ct);
        return result;
    }

    public Task<SingleSegmentTranslationTextResult> TranslateSingleSegmentTextAsync(
        SingleSegmentTranslationTextRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new SingleSegmentTranslationTextResult(
            true,
            $"{request.SourceText} (en)",
            request.SourceLanguage,
            request.TargetLanguage,
            null));

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) => new(true, "Ready");
    public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) => Task.FromResult(true);
}

public sealed class FakeTtsRegistry : ITtsRegistry
{
    private readonly ITtsProvider _provider;

    public FakeTtsRegistry(ITtsProvider? provider = null)
    {
        _provider = provider ?? new FakeTtsProvider();
    }

    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [new("fake-tts", "Fake TTS", false, null, ["default"])];

    public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
        ["default"];

    public ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
        _provider;

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
    public bool CombineAudioSegmentsAsyncCalled { get; private set; }
    public bool ComposeTimelineDubAsyncCalled { get; private set; }
    public bool MixDubOverAmbianceAsyncCalled { get; private set; }
    public int TimeStretchCallCount { get; private set; }
    /// <summary>Last <c>ambianceGainDb</c> value the fake received, so tests can assert
    /// the coordinator forwarded <c>AppSettings.AmbianceMixDb</c> correctly.</summary>
    public double? LastAmbianceGainDb { get; private set; }
    public bool SkipTimelineOutputCreation { get; set; }
    public bool SkipMixedOutputCreation { get; set; }
    public bool ThrowOnMixDubOverAmbiance { get; set; }
    public bool TimeStretchShouldSucceed { get; set; }

    public async Task CombineAudioSegmentsAsync(IReadOnlyList<string> segmentAudioPaths, string outputAudioPath, CancellationToken cancellationToken)
    {
        CombineAudioSegmentsAsyncCalled = true;
        var dir = Path.GetDirectoryName(outputAudioPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(outputAudioPath, [0xAA, 0xBB], cancellationToken);
    }

    public async Task ComposeTimelineDubAsync(
        IReadOnlyList<TimelineDubSegment> segments,
        string outputAudioPath,
        CancellationToken cancellationToken)
    {
        ComposeTimelineDubAsyncCalled = true;
        var dir = Path.GetDirectoryName(outputAudioPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (SkipTimelineOutputCreation)
            return;
        await File.WriteAllBytesAsync(outputAudioPath, [0xAB, 0xCD], cancellationToken);
    }

    public async Task MixDubOverAmbianceAsync(
        string dubbedAudioPath,
        string ambianceAudioPath,
        string outputAudioPath,
        double ambianceGainDb,
        CancellationToken cancellationToken)
    {
        MixDubOverAmbianceAsyncCalled = true;
        LastAmbianceGainDb = ambianceGainDb;
        if (ThrowOnMixDubOverAmbiance)
            throw new InvalidOperationException("PLACEHOLDER(test-fake): simulated mix failure");

        var dir = Path.GetDirectoryName(outputAudioPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (SkipMixedOutputCreation)
            return;
        await File.WriteAllBytesAsync(outputAudioPath, [0xDE, 0xAD], cancellationToken);
    }

    public async Task ExtractAudioClipAsync(string inputPath, string outputPath, double startTimeSeconds, double durationSeconds, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(outputPath, [0xCC, 0xDD], cancellationToken);
    }

    /// <summary>
    /// Writes a small placeholder audio file to the specified output path and ensures the output directory exists; the input path is not used.
    /// </summary>
    /// <param name="inputPath">The source audio path (ignored by this fake implementation).</param>
    /// <param name="outputPath">The file path to write the placeholder audio bytes to; the method creates the directory if needed.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous write operation.</param>
    public async Task ExtractFullAudioAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(outputPath, [0xEE, 0xFF], cancellationToken);
    }

    public Task<bool> TimeStretchAsync(
        string inputPath,
        string outputPath,
        double targetDurationSeconds,
        double minRatio = DubTimingDefaults.StretchMinTempoRatio,
        double maxRatio = DubTimingDefaults.StretchMaxTempoRatio,
        CancellationToken cancellationToken = default)
    {
        TimeStretchCallCount++;
        if (!TimeStretchShouldSucceed)
            return Task.FromResult(false);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(outputPath, [0x11, 0x22]);
        return Task.FromResult(true);
    }

    public Task<double?> ProbeDurationAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.FromResult<double?>(null);
}
