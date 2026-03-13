using BabelPlayer.App;
using BabelPlayer.Core;
using CoreAppDiagnosticsSnapshot = BabelPlayer.Core.AppDiagnosticsSnapshot;
using CorePlaybackDiagnosticsSummary = BabelPlayer.Core.PlaybackDiagnosticsSummary;
using CoreQueueDiagnosticsSummary = BabelPlayer.Core.QueueDiagnosticsSummary;
using CoreSubtitleWorkflowDiagnosticsSummary = BabelPlayer.Core.SubtitleWorkflowDiagnosticsSummary;

namespace BabelPlayer.App.Tests;

public sealed class LoggingInfrastructureTests
{
    [Fact]
    public void AppDiagnosticsContext_StoresCompactSnapshot()
    {
        var context = new AppDiagnosticsContext();

        context.UpdateWindowMode("Fullscreen");
        context.UpdatePlayback(new CorePlaybackDiagnosticsSummary
        {
            CurrentMediaPath = @"C:\media\clip.mp4",
            CurrentMediaDisplayName = "clip.mp4",
            IsPaused = false,
            Position = TimeSpan.FromSeconds(12),
            Duration = TimeSpan.FromSeconds(95),
            Volume = 0.65,
            IsMuted = false,
            ActiveHardwareDecoder = "d3d11va",
            VideoWidth = 1920,
            VideoHeight = 1080,
            VideoDisplayWidth = 1920,
            VideoDisplayHeight = 1080
        });
        context.UpdateQueue(new CoreQueueDiagnosticsSummary
        {
            NowPlayingDisplayName = "clip.mp4",
            NowPlayingPath = @"C:\media\clip.mp4",
            UpNextCount = 3,
            HistoryCount = 1
        });
        context.UpdateSubtitleWorkflow(new CoreSubtitleWorkflowDiagnosticsSummary
        {
            SubtitleSource = "Generated",
            IsCaptionGenerationInProgress = true,
            SelectedTranscriptionModelKey = "local:base-multilingual",
            SelectedTranslationModelKey = "openai:gpt-5-mini",
            IsTranslationEnabled = true,
            SourceLanguage = "es",
            OverlayStatus = "Generating subtitles..."
        });

        var snapshot = context.Snapshot;
        Assert.Equal("Fullscreen", snapshot.WindowMode);
        Assert.Equal(@"C:\media\clip.mp4", snapshot.Playback.CurrentMediaPath);
        Assert.Equal(3, snapshot.Queue.UpNextCount);
        Assert.Equal("Generated", snapshot.SubtitleWorkflow.SubtitleSource);
        Assert.True(snapshot.SubtitleWorkflow.IsCaptionGenerationInProgress);
    }

    [Fact]
    public void BabelLogManager_WritesFormattedLogAndRedactsSensitiveContext()
    {
        var directory = CreateTempDirectory();
        using var manager = new BabelLogManager(new BabelLogOptions
        {
            LogDirectory = directory,
            MinimumLevel = BabelLogLevel.Debug,
            MaxFileBytes = 1024 * 1024,
            MaxFilesPerStream = 3
        });

        var logger = manager.CreateLogger("tests.logging");
        logger.LogError(
            "Persistent logging works.",
            new InvalidOperationException("boom"),
            BabelLogContext.Create(("apiKey", "super-secret"), ("path", @"C:\media\clip.mp4")));

        manager.Flush(TimeSpan.FromSeconds(1));

        var content = File.ReadAllText(Path.Combine(directory, "app.log"));
        Assert.Contains("ERROR | tests.logging | Persistent logging works.", content);
        Assert.Contains("apiKey=[REDACTED]", content);
        Assert.Contains(@"path=C:\media\clip.mp4", content);
        Assert.Contains("InvalidOperationException: boom", content);
    }

    [Fact]
    public void BabelLogManager_RollsLogFiles_WhenSizeLimitIsExceeded()
    {
        var directory = CreateTempDirectory();
        using var manager = new BabelLogManager(new BabelLogOptions
        {
            LogDirectory = directory,
            MinimumLevel = BabelLogLevel.Debug,
            MaxFileBytes = 220,
            MaxFilesPerStream = 3
        });

        var logger = manager.CreateLogger("tests.roll");
        logger.LogInfo(new string('A', 180));
        logger.LogInfo(new string('B', 180));
        manager.Flush(TimeSpan.FromSeconds(1));

        Assert.True(File.Exists(Path.Combine(directory, "app.log")));
        Assert.True(File.Exists(Path.Combine(directory, "app.1.log")));
    }

    [Fact]
    public void BabelLogManager_DoesNotThrow_WhenLogPathIsInvalid()
    {
        using var manager = new BabelLogManager(new BabelLogOptions
        {
            LogDirectory = Path.Combine(Path.GetTempPath(), "BabelPlayer.Tests", "invalid<logs"),
            MinimumLevel = BabelLogLevel.Debug
        });

        var logger = manager.CreateLogger("tests.invalid-path");
        var exception = Record.Exception(() =>
        {
            logger.LogInfo("This should not throw.");
            manager.Flush(TimeSpan.FromMilliseconds(200));
        });

        Assert.Null(exception);
    }

    [Fact]
    public void BabelLogManager_EmitsOverflowWarning_WhenQueueCapacityIsExceeded()
    {
        var directory = CreateTempDirectory();
        using var manager = new BabelLogManager(new BabelLogOptions
        {
            LogDirectory = directory,
            MinimumLevel = BabelLogLevel.Debug,
            MaxFileBytes = 1024 * 1024,
            MaxFilesPerStream = 3,
            QueueCapacity = 1
        });

        var logger = manager.CreateLogger("tests.overflow");
        for (var index = 0; index < 10000; index++)
        {
            logger.LogInfo($"overflow-{index}");
        }

        manager.Flush(TimeSpan.FromSeconds(5));

        var content = File.ReadAllText(Path.Combine(directory, "app.log"));
        Assert.Contains("WARNING | logging | Dropped", content);
        Assert.Contains("queueCapacity=1", content);
    }

    [Fact]
    public void CrashWriter_WritesSnapshotContextToCrashLog()
    {
        var directory = CreateTempDirectory();
        using var manager = new BabelLogManager(new BabelLogOptions
        {
            LogDirectory = directory,
            MinimumLevel = BabelLogLevel.Info
        });

        manager.WriteUnhandledException(
            "app.unhandled",
            new InvalidOperationException("fatal"),
            new CoreAppDiagnosticsSnapshot
            {
                WindowMode = "Standard",
                Playback = new CorePlaybackDiagnosticsSummary
                {
                    CurrentMediaPath = @"C:\media\clip.mp4",
                    CurrentMediaDisplayName = "clip.mp4",
                    Position = TimeSpan.FromSeconds(14),
                    Duration = TimeSpan.FromSeconds(90)
                },
                Queue = new CoreQueueDiagnosticsSummary
                {
                    NowPlayingDisplayName = "clip.mp4",
                    UpNextCount = 2,
                    HistoryCount = 1
                },
            SubtitleWorkflow = new CoreSubtitleWorkflowDiagnosticsSummary
            {
                SubtitleSource = "Sidecar",
                SelectedTranscriptionModelKey = "local:base-multilingual",
                SelectedTranslationModelKey = "cloud:gpt-5-mini",
                IsTranslationEnabled = true,
                SourceLanguage = "ja"
            }
            });

        var content = File.ReadAllText(Path.Combine(directory, "crash.log"));
        Assert.Contains("CRITICAL | app.unhandled | Unhandled exception.", content);
        Assert.Contains(@"mediaPath=C:\media\clip.mp4", content);
        Assert.Contains("subtitleSource=Sidecar", content);
        Assert.Contains("InvalidOperationException: fatal", content);
    }

    [Fact]
    public async Task DefaultCaptionGenerator_LogsProviderFailure()
    {
        var factory = new RecordingLogFactory();
        var generator = new DefaultCaptionGenerator(
            new ProviderAvailabilityContext(new CredentialFacade(), Environment.GetEnvironmentVariable, factory),
            new TranscriptionProviderRegistry([new ThrowingTranscriptionProvider()]),
            factory);

        await Assert.ThrowsAsync<AggregateException>(() => generator.GenerateCaptionsAsync(
            @"C:\media\clip.mp4",
            SubtitleWorkflowCatalog.GetTranscriptionModel("local:base-multilingual"),
            null,
            null,
            null,
            CancellationToken.None));

        Assert.Contains(factory.Entries, entry =>
            entry.Category == "subtitles.captions"
            && entry.Level == BabelLogLevel.Warning
            && entry.Message.Contains("provider failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProviderBackedSubtitleTranslator_LogsTranslationFailure()
    {
        var factory = new RecordingLogFactory();
        var translator = new ProviderBackedSubtitleTranslator(
            new ProviderAvailabilityContext(new CredentialFacade(), Environment.GetEnvironmentVariable, factory),
            new TranslationProviderRegistry([new ThrowingTranslationProvider()]),
            factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => translator.TranslateBatchAsync(
            SubtitleWorkflowCatalog.GetTranslationModel("cloud:gpt-5-mini"),
            ["hola"],
            CancellationToken.None));

        Assert.Contains(factory.Entries, entry =>
            entry.Category == "subtitles.translation"
            && entry.Level == BabelLogLevel.Error
            && entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "BabelPlayer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ThrowingTranscriptionProvider : ITranscriptionProvider
    {
        public string Id => "throwing";

        public TranscriptionProvider Provider => TranscriptionProvider.Local;

        public bool CanHandle(TranscriptionModelSelection selection) => true;

        public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context) => true;

        public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(TranscriptionRequest request, ProviderAvailabilityContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("caption failure");
    }

    private sealed class ThrowingTranslationProvider : ITranslationProvider
    {
        public TranslationProvider Provider => TranslationProvider.OpenAi;

        public bool IsConfigured(ProviderAvailabilityContext context) => true;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(TranslationRequest request, ProviderAvailabilityContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("translation failure");
    }

    private sealed class RecordingLogFactory : IBabelLogFactory
    {
        public BabelLogLevel MinimumLevel => BabelLogLevel.Debug;

        public List<LogEntry> Entries { get; } = [];

        public IBabelLogger CreateLogger(string category) => new RecordingLogger(category, Entries);

        public sealed record LogEntry(string Category, BabelLogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?>? Context);

        private sealed class RecordingLogger : IBabelLogger
        {
            private readonly List<LogEntry> _entries;

            public RecordingLogger(string category, List<LogEntry> entries)
            {
                Category = category;
                _entries = entries;
            }

            public string Category { get; }

            public bool IsEnabled(BabelLogLevel level) => true;

            public void Log(BabelLogLevel level, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
            {
                _entries.Add(new LogEntry(Category, level, message, exception, context));
            }
        }
    }
}
