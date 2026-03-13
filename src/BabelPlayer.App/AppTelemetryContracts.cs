using BabelPlayer.Core;

namespace BabelPlayer.App;

public enum AppLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}

public interface IAppLogger
{
    string Category { get; }
    bool IsEnabled(AppLogLevel level);
    void Log(AppLogLevel level, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null);
}

public interface IAppLogFactory
{
    AppLogLevel MinimumLevel { get; }
    IAppLogger CreateLogger(string category);
}

public sealed record PlaybackDiagnosticsSummary
{
    public string? CurrentMediaPath { get; init; }
    public string? CurrentMediaDisplayName { get; init; }
    public bool IsPaused { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public double Volume { get; init; }
    public bool IsMuted { get; init; }
    public string ActiveHardwareDecoder { get; init; } = string.Empty;
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public int VideoDisplayWidth { get; init; }
    public int VideoDisplayHeight { get; init; }
}

public sealed record QueueDiagnosticsSummary
{
    public string? NowPlayingDisplayName { get; init; }
    public string? NowPlayingPath { get; init; }
    public int UpNextCount { get; init; }
    public int HistoryCount { get; init; }
}

public sealed record SubtitleWorkflowDiagnosticsSummary
{
    public string SubtitleSource { get; init; } = string.Empty;
    public bool IsCaptionGenerationInProgress { get; init; }
    public string SelectedTranscriptionModelKey { get; init; } = string.Empty;
    public string SelectedTranslationModelKey { get; init; } = string.Empty;
    public bool IsTranslationEnabled { get; init; }
    public string SourceLanguage { get; init; } = string.Empty;
    public string OverlayStatus { get; init; } = string.Empty;
}

public sealed record AppDiagnosticsSnapshot
{
    public string WindowMode { get; init; } = string.Empty;
    public PlaybackDiagnosticsSummary Playback { get; init; } = new();
    public QueueDiagnosticsSummary Queue { get; init; } = new();
    public SubtitleWorkflowDiagnosticsSummary SubtitleWorkflow { get; init; } = new();
}

public sealed class NullAppLogFactory : IAppLogFactory
{
    public static NullAppLogFactory Instance { get; } = new();

    private NullAppLogFactory()
    {
    }

    public AppLogLevel MinimumLevel => AppLogLevel.Critical;

    public IAppLogger CreateLogger(string category) => new NullAppLogger(category);

    private sealed class NullAppLogger : IAppLogger
    {
        public NullAppLogger(string category)
        {
            Category = category;
        }

        public string Category { get; }

        public bool IsEnabled(AppLogLevel level) => false;

        public void Log(AppLogLevel level, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        {
        }
    }
}

public interface IAppDiagnosticsState
{
    AppDiagnosticsSnapshot Snapshot { get; }
    void UpdatePlayback(PlaybackDiagnosticsSummary playback);
    void UpdateQueue(QueueDiagnosticsSummary queue);
    void UpdateSubtitleWorkflow(SubtitleWorkflowDiagnosticsSummary subtitleWorkflow);
    void UpdateWindowMode(string? windowMode);
}

public interface IAppTelemetryBootstrap : IDisposable
{
    IAppLogFactory LogFactory { get; }
    IAppDiagnosticsState DiagnosticsState { get; }
    void WriteUnhandledException(string category, Exception exception, IReadOnlyDictionary<string, object?>? context = null);
    void Flush(TimeSpan timeout);
}

public static class AppLoggerExtensions
{
    public static void LogDebug(this IAppLogger logger, string message, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(AppLogLevel.Debug, message, null, context);

    public static void LogInfo(this IAppLogger logger, string message, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(AppLogLevel.Info, message, null, context);

    public static void LogWarning(this IAppLogger logger, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(AppLogLevel.Warning, message, exception, context);

    public static void LogError(this IAppLogger logger, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(AppLogLevel.Error, message, exception, context);

    public static void LogCritical(this IAppLogger logger, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(AppLogLevel.Critical, message, exception, context);
}

public static class AppLogContext
{
    public static IReadOnlyDictionary<string, object?> Create(params (string Key, object? Value)[] entries)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                dictionary[key] = value;
            }
        }

        return dictionary;
    }
}

public sealed class AppTelemetryBootstrap : IAppTelemetryBootstrap
{
    private readonly BabelLogManager _logManager;
    private readonly AppDiagnosticsContext _diagnosticsContext;
    private readonly IAppLogFactory _logFactory;
    private readonly IAppDiagnosticsState _diagnosticsState;

    public AppTelemetryBootstrap(BabelLogOptions? options = null)
    {
        _logManager = new BabelLogManager(options ?? BabelLogOptions.CreateDefault());
        _diagnosticsContext = new AppDiagnosticsContext();
        _logFactory = new AppLogFactoryAdapter(_logManager);
        _diagnosticsState = new AppDiagnosticsStateAdapter(_diagnosticsContext);
    }

    public IAppLogFactory LogFactory => _logFactory;

    public IAppDiagnosticsState DiagnosticsState => _diagnosticsState;

    public void WriteUnhandledException(string category, Exception exception, IReadOnlyDictionary<string, object?>? context = null)
    {
        _logManager.WriteUnhandledException(category, exception, _diagnosticsContext.Snapshot, context);
    }

    public void Flush(TimeSpan timeout) => _logManager.Flush(timeout);

    public void Dispose()
    {
        _logManager.Dispose();
    }

    private sealed class AppLogFactoryAdapter : IAppLogFactory
    {
        private readonly IBabelLogFactory _inner;

        public AppLogFactoryAdapter(IBabelLogFactory inner)
        {
            _inner = inner;
        }

        public AppLogLevel MinimumLevel => (AppLogLevel)_inner.MinimumLevel;

        public IAppLogger CreateLogger(string category) => new AppLoggerAdapter(_inner.CreateLogger(category));
    }

    private sealed class AppLoggerAdapter : IAppLogger
    {
        private readonly IBabelLogger _inner;

        public AppLoggerAdapter(IBabelLogger inner)
        {
            _inner = inner;
        }

        public string Category => _inner.Category;

        public bool IsEnabled(AppLogLevel level) => _inner.IsEnabled((BabelLogLevel)level);

        public void Log(AppLogLevel level, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        {
            _inner.Log((BabelLogLevel)level, message, exception, context);
        }
    }

    private sealed class AppDiagnosticsStateAdapter : IAppDiagnosticsState
    {
        private readonly BabelPlayer.Core.IAppDiagnosticsContext _inner;

        public AppDiagnosticsStateAdapter(BabelPlayer.Core.IAppDiagnosticsContext inner)
        {
            _inner = inner;
        }

        public AppDiagnosticsSnapshot Snapshot => new()
        {
            WindowMode = _inner.Snapshot.WindowMode,
            Playback = new PlaybackDiagnosticsSummary
            {
                CurrentMediaPath = _inner.Snapshot.Playback.CurrentMediaPath,
                CurrentMediaDisplayName = _inner.Snapshot.Playback.CurrentMediaDisplayName,
                IsPaused = _inner.Snapshot.Playback.IsPaused,
                Position = _inner.Snapshot.Playback.Position,
                Duration = _inner.Snapshot.Playback.Duration,
                Volume = _inner.Snapshot.Playback.Volume,
                IsMuted = _inner.Snapshot.Playback.IsMuted,
                ActiveHardwareDecoder = _inner.Snapshot.Playback.ActiveHardwareDecoder,
                VideoWidth = _inner.Snapshot.Playback.VideoWidth,
                VideoHeight = _inner.Snapshot.Playback.VideoHeight,
                VideoDisplayWidth = _inner.Snapshot.Playback.VideoDisplayWidth,
                VideoDisplayHeight = _inner.Snapshot.Playback.VideoDisplayHeight
            },
            Queue = new QueueDiagnosticsSummary
            {
                NowPlayingDisplayName = _inner.Snapshot.Queue.NowPlayingDisplayName,
                NowPlayingPath = _inner.Snapshot.Queue.NowPlayingPath,
                UpNextCount = _inner.Snapshot.Queue.UpNextCount,
                HistoryCount = _inner.Snapshot.Queue.HistoryCount
            },
            SubtitleWorkflow = new SubtitleWorkflowDiagnosticsSummary
            {
                SubtitleSource = _inner.Snapshot.SubtitleWorkflow.SubtitleSource,
                IsCaptionGenerationInProgress = _inner.Snapshot.SubtitleWorkflow.IsCaptionGenerationInProgress,
                SelectedTranscriptionModelKey = _inner.Snapshot.SubtitleWorkflow.SelectedTranscriptionModelKey,
                SelectedTranslationModelKey = _inner.Snapshot.SubtitleWorkflow.SelectedTranslationModelKey,
                IsTranslationEnabled = _inner.Snapshot.SubtitleWorkflow.IsTranslationEnabled,
                SourceLanguage = _inner.Snapshot.SubtitleWorkflow.SourceLanguage,
                OverlayStatus = _inner.Snapshot.SubtitleWorkflow.OverlayStatus
            }
        };

        public void UpdatePlayback(PlaybackDiagnosticsSummary playback) => _inner.UpdatePlayback(new BabelPlayer.Core.PlaybackDiagnosticsSummary
        {
            CurrentMediaPath = playback.CurrentMediaPath,
            CurrentMediaDisplayName = playback.CurrentMediaDisplayName,
            IsPaused = playback.IsPaused,
            Position = playback.Position,
            Duration = playback.Duration,
            Volume = playback.Volume,
            IsMuted = playback.IsMuted,
            ActiveHardwareDecoder = playback.ActiveHardwareDecoder,
            VideoWidth = playback.VideoWidth,
            VideoHeight = playback.VideoHeight,
            VideoDisplayWidth = playback.VideoDisplayWidth,
            VideoDisplayHeight = playback.VideoDisplayHeight
        });

        public void UpdateQueue(QueueDiagnosticsSummary queue) => _inner.UpdateQueue(new BabelPlayer.Core.QueueDiagnosticsSummary
        {
            NowPlayingDisplayName = queue.NowPlayingDisplayName,
            NowPlayingPath = queue.NowPlayingPath,
            UpNextCount = queue.UpNextCount,
            HistoryCount = queue.HistoryCount
        });

        public void UpdateSubtitleWorkflow(SubtitleWorkflowDiagnosticsSummary subtitleWorkflow) => _inner.UpdateSubtitleWorkflow(new BabelPlayer.Core.SubtitleWorkflowDiagnosticsSummary
        {
            SubtitleSource = subtitleWorkflow.SubtitleSource,
            IsCaptionGenerationInProgress = subtitleWorkflow.IsCaptionGenerationInProgress,
            SelectedTranscriptionModelKey = subtitleWorkflow.SelectedTranscriptionModelKey,
            SelectedTranslationModelKey = subtitleWorkflow.SelectedTranslationModelKey,
            IsTranslationEnabled = subtitleWorkflow.IsTranslationEnabled,
            SourceLanguage = subtitleWorkflow.SourceLanguage,
            OverlayStatus = subtitleWorkflow.OverlayStatus
        });

        public void UpdateWindowMode(string? windowMode) => _inner.UpdateWindowMode(windowMode);
    }
}
