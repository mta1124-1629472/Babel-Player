using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace BabelPlayer.Core;

public enum BabelLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}

public sealed record BabelLogOptions
{
    public string LogDirectory { get; init; } = Path.Combine(BabelAppDataPaths.GetAppDataDirectory(), "logs");
    public BabelLogLevel MinimumLevel { get; init; } = BabelLogLevel.Info;
    public long MaxFileBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxFilesPerStream { get; init; } = 5;

    public static BabelLogOptions CreateDefault() => new();
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

public interface IAppDiagnosticsContext
{
    AppDiagnosticsSnapshot Snapshot { get; }

    void UpdatePlayback(PlaybackDiagnosticsSummary playback);

    void UpdateQueue(QueueDiagnosticsSummary queue);

    void UpdateSubtitleWorkflow(SubtitleWorkflowDiagnosticsSummary subtitleWorkflow);

    void UpdateWindowMode(string? windowMode);
}

public sealed class AppDiagnosticsContext : IAppDiagnosticsContext
{
    private readonly object _sync = new();
    private AppDiagnosticsSnapshot _snapshot = new();

    public AppDiagnosticsSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public void UpdatePlayback(PlaybackDiagnosticsSummary playback)
    {
        ArgumentNullException.ThrowIfNull(playback);

        lock (_sync)
        {
            _snapshot = _snapshot with { Playback = playback };
        }
    }

    public void UpdateQueue(QueueDiagnosticsSummary queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        lock (_sync)
        {
            _snapshot = _snapshot with { Queue = queue };
        }
    }

    public void UpdateSubtitleWorkflow(SubtitleWorkflowDiagnosticsSummary subtitleWorkflow)
    {
        ArgumentNullException.ThrowIfNull(subtitleWorkflow);

        lock (_sync)
        {
            _snapshot = _snapshot with { SubtitleWorkflow = subtitleWorkflow };
        }
    }

    public void UpdateWindowMode(string? windowMode)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with { WindowMode = windowMode?.Trim() ?? string.Empty };
        }
    }
}

public interface IBabelLogger
{
    string Category { get; }

    bool IsEnabled(BabelLogLevel level);

    void Log(
        BabelLogLevel level,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? context = null);
}

public interface IBabelLogFactory
{
    BabelLogLevel MinimumLevel { get; }

    IBabelLogger CreateLogger(string category);
}

public interface ICrashReportWriter
{
    void WriteUnhandledException(
        string category,
        Exception exception,
        AppDiagnosticsSnapshot snapshot,
        IReadOnlyDictionary<string, object?>? context = null);

    void Flush(TimeSpan timeout);
}

public static class BabelLoggerExtensions
{
    public static void LogDebug(this IBabelLogger logger, string message, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(BabelLogLevel.Debug, message, null, context);

    public static void LogInfo(this IBabelLogger logger, string message, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(BabelLogLevel.Info, message, null, context);

    public static void LogWarning(this IBabelLogger logger, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(BabelLogLevel.Warning, message, exception, context);

    public static void LogError(this IBabelLogger logger, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(BabelLogLevel.Error, message, exception, context);

    public static void LogCritical(this IBabelLogger logger, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        => logger.Log(BabelLogLevel.Critical, message, exception, context);
}

public sealed class NullBabelLogFactory : IBabelLogFactory
{
    private static readonly IBabelLogger LoggerInstance = new NullBabelLogger();

    private NullBabelLogFactory()
    {
    }

    public static NullBabelLogFactory Instance { get; } = new();

    public BabelLogLevel MinimumLevel => BabelLogLevel.Critical;

    public IBabelLogger CreateLogger(string category) => LoggerInstance;

    private sealed class NullBabelLogger : IBabelLogger
    {
        public string Category => "null";

        public bool IsEnabled(BabelLogLevel level) => false;

        public void Log(BabelLogLevel level, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        {
        }
    }
}

public static class BabelLogContext
{
    public static IReadOnlyDictionary<string, object?> Create(params (string Key, object? Value)[] entries)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            dictionary[key] = value;
        }

        return new ReadOnlyDictionary<string, object?>(dictionary);
    }
}

public sealed class BabelLogManager : IBabelLogFactory, ICrashReportWriter, IDisposable
{
    private readonly BabelLogOptions _options;
    private readonly Channel<LogEntry> _entries;
    private readonly object _fileSync = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _writerTask;
    private long _pendingWriteCount;
    private int _disposed;

    public BabelLogManager(BabelLogOptions? options = null)
    {
        _options = options ?? BabelLogOptions.CreateDefault();
        _entries = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(ProcessQueueAsync);
    }

    public BabelLogLevel MinimumLevel => _options.MinimumLevel;

    public IBabelLogger CreateLogger(string category)
    {
        return new BabelLogger(this, category);
    }

    public void Log(
        string category,
        BabelLogLevel level,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? context = null)
    {
        if (level < _options.MinimumLevel || string.IsNullOrWhiteSpace(message) || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var entry = new LogEntry("app", DateTimeOffset.Now, category, level, message, exception, context);
        try
        {
            Interlocked.Increment(ref _pendingWriteCount);
            if (!_entries.Writer.TryWrite(entry))
            {
                Interlocked.Decrement(ref _pendingWriteCount);
            }
        }
        catch
        {
            Interlocked.Decrement(ref _pendingWriteCount);
        }
    }

    public void WriteUnhandledException(
        string category,
        Exception exception,
        AppDiagnosticsSnapshot snapshot,
        IReadOnlyDictionary<string, object?>? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var mergedContext = MergeContext(context, BuildCrashContext(snapshot));
        var entry = new LogEntry("crash", DateTimeOffset.Now, category, BabelLogLevel.Critical, "Unhandled exception.", exception, mergedContext);
        WriteEntrySynchronously(entry);
    }

    public void Flush(TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (Interlocked.Read(ref _pendingWriteCount) > 0 && deadline.Elapsed < timeout)
        {
            Thread.Sleep(25);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _entries.Writer.TryComplete();
            Flush(TimeSpan.FromSeconds(2));
            _disposeCts.Cancel();
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        finally
        {
            _disposeCts.Dispose();
        }
    }

    internal static IReadOnlyDictionary<string, object?> MergeContext(
        IReadOnlyDictionary<string, object?>? primary,
        IReadOnlyDictionary<string, object?>? secondary)
    {
        if ((primary is null || primary.Count == 0) && (secondary is null || secondary.Count == 0))
        {
            return BabelLogContext.Create();
        }

        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (secondary is not null)
        {
            foreach (var pair in secondary)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        if (primary is not null)
        {
            foreach (var pair in primary)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return new ReadOnlyDictionary<string, object?>(merged);
    }

    internal static IReadOnlyDictionary<string, object?> BuildCrashContext(AppDiagnosticsSnapshot snapshot)
    {
        return BabelLogContext.Create(
            ("windowMode", snapshot.WindowMode),
            ("mediaPath", snapshot.Playback.CurrentMediaPath),
            ("mediaDisplayName", snapshot.Playback.CurrentMediaDisplayName),
            ("playbackPaused", snapshot.Playback.IsPaused),
            ("playbackPosition", snapshot.Playback.Position),
            ("playbackDuration", snapshot.Playback.Duration),
            ("volume", snapshot.Playback.Volume),
            ("muted", snapshot.Playback.IsMuted),
            ("activeHardwareDecoder", snapshot.Playback.ActiveHardwareDecoder),
            ("videoWidth", snapshot.Playback.VideoWidth),
            ("videoHeight", snapshot.Playback.VideoHeight),
            ("videoDisplayWidth", snapshot.Playback.VideoDisplayWidth),
            ("videoDisplayHeight", snapshot.Playback.VideoDisplayHeight),
            ("queueNowPlaying", snapshot.Queue.NowPlayingDisplayName),
            ("queueNowPlayingPath", snapshot.Queue.NowPlayingPath),
            ("queueUpNextCount", snapshot.Queue.UpNextCount),
            ("queueHistoryCount", snapshot.Queue.HistoryCount),
            ("subtitleSource", snapshot.SubtitleWorkflow.SubtitleSource),
            ("captionGenerationInProgress", snapshot.SubtitleWorkflow.IsCaptionGenerationInProgress),
            ("transcriptionModel", snapshot.SubtitleWorkflow.SelectedTranscriptionModelKey),
            ("translationModel", snapshot.SubtitleWorkflow.SelectedTranslationModelKey),
            ("translationEnabled", snapshot.SubtitleWorkflow.IsTranslationEnabled),
            ("sourceLanguage", snapshot.SubtitleWorkflow.SourceLanguage),
            ("overlayStatus", snapshot.SubtitleWorkflow.OverlayStatus),
            ("processId", Environment.ProcessId),
            ("threadId", Environment.CurrentManagedThreadId));
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (await _entries.Reader.WaitToReadAsync(_disposeCts.Token))
            {
                while (_entries.Reader.TryRead(out var entry))
                {
                    WriteEntrySynchronously(entry);
                    Interlocked.Decrement(ref _pendingWriteCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void WriteEntrySynchronously(LogEntry entry)
    {
        try
        {
            lock (_fileSync)
            {
                Directory.CreateDirectory(_options.LogDirectory);
                var logPath = Path.Combine(_options.LogDirectory, $"{entry.StreamName}.log");
                RotateIfNeeded(logPath, entry);
                File.AppendAllText(logPath, FormatEntry(entry), Encoding.UTF8);
            }
        }
        catch (Exception writeException)
        {
            TryWriteEmergencyFailure(entry, writeException);
        }
    }

    private void RotateIfNeeded(string logPath, LogEntry entry)
    {
        var projectedLength = Encoding.UTF8.GetByteCount(FormatEntry(entry));
        if (File.Exists(logPath))
        {
            var info = new FileInfo(logPath);
            if (info.Length + projectedLength < _options.MaxFileBytes)
            {
                return;
            }
        }

        var maxIndex = Math.Max(_options.MaxFilesPerStream - 1, 0);
        var oldest = Path.Combine(_options.LogDirectory, $"{entry.StreamName}.{maxIndex}.log");
        if (maxIndex > 0 && File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = maxIndex - 1; index >= 1; index--)
        {
            var current = Path.Combine(_options.LogDirectory, $"{entry.StreamName}.{index}.log");
            var next = Path.Combine(_options.LogDirectory, $"{entry.StreamName}.{index + 1}.log");
            if (File.Exists(current))
            {
                File.Move(current, next, overwrite: true);
            }
        }

        if (maxIndex > 0 && File.Exists(logPath))
        {
            File.Move(logPath, Path.Combine(_options.LogDirectory, $"{entry.StreamName}.1.log"), overwrite: true);
        }
    }

    private static string FormatEntry(LogEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(entry.Level.ToString().ToUpperInvariant())
            .Append(" | ")
            .Append(string.IsNullOrWhiteSpace(entry.Category) ? "app" : entry.Category.Trim())
            .Append(" | ")
            .Append(SanitizeInline(entry.Message));

        var redactedContext = BabelLogRedaction.Redact(entry.Context);
        foreach (var pair in redactedContext)
        {
            builder.Append(" | ")
                .Append(pair.Key)
                .Append('=')
                .Append(FormatContextValue(pair.Value));
        }

        if (entry.Exception is not null)
        {
            builder.AppendLine();
            builder.Append(entry.Exception);
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private void TryWriteEmergencyFailure(LogEntry originalEntry, Exception writeException)
    {
        try
        {
            Directory.CreateDirectory(_options.LogDirectory);
            var emergencyPath = Path.Combine(_options.LogDirectory, "crash.log");
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | CRITICAL | logging | log-write-failed | stream={originalEntry.StreamName} | category={originalEntry.Category} | error={FormatContextValue(writeException.Message)}{Environment.NewLine}";
            File.AppendAllText(emergencyPath, line, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string FormatContextValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var text = value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };

        return text.IndexOfAny([' ', '|', '\r', '\n', '=']) >= 0
            ? $"\"{SanitizeInline(text)}\""
            : SanitizeInline(text);
    }

    private static string SanitizeInline(string value)
    {
        return value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }

    private sealed record LogEntry(
        string StreamName,
        DateTimeOffset Timestamp,
        string Category,
        BabelLogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?>? Context);

    private sealed class BabelLogger : IBabelLogger
    {
        private readonly BabelLogManager _owner;

        public BabelLogger(BabelLogManager owner, string category)
        {
            _owner = owner;
            Category = string.IsNullOrWhiteSpace(category) ? "app" : category.Trim();
        }

        public string Category { get; }

        public bool IsEnabled(BabelLogLevel level) => level >= _owner.MinimumLevel;

        public void Log(BabelLogLevel level, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? context = null)
        {
            _owner.Log(Category, level, message, exception, context);
        }
    }
}

public static class BabelLogRedaction
{
    private static readonly string[] SensitiveKeys =
    [
        "apiKey",
        "authorization",
        "bearer",
        "password",
        "secret",
        "token"
    ];

    public static IReadOnlyDictionary<string, object?> Redact(IReadOnlyDictionary<string, object?>? context)
    {
        if (context is null || context.Count == 0)
        {
            return BabelLogContext.Create();
        }

        var sanitized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in context)
        {
            sanitized[pair.Key] = IsSensitiveKey(pair.Key) ? "[REDACTED]" : pair.Value;
        }

        return new ReadOnlyDictionary<string, object?>(sanitized);
    }

    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return SensitiveKeys.Any(sensitive => key.Contains(sensitive, StringComparison.OrdinalIgnoreCase));
    }
}

public static class BabelAppDataPaths
{
    public static string GetAppDataDirectory()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer");
        Directory.CreateDirectory(path);
        return path;
    }
}
