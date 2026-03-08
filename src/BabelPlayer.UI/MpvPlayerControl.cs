using BabelPlayer.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace BabelPlayer.UI;

public sealed class MpvPlayerControl : HwndHost
{
    private readonly MpvPlaybackEngine _engine = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private PlaybackStateSnapshot _snapshot = new();
    private Uri? _source;
    private double _volume = 0.8;
    private string? _pendingPath;
    private bool _initialized;
    private IntPtr _hwnd;

    public event RoutedEventHandler? MediaOpened;
    public event RoutedEventHandler? MediaEnded;
    public event Action<string>? MediaFailed;
    public event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgress;

    public MpvPlayerControl()
    {
        _engine.OnStateChanged += snapshot =>
        {
            _snapshot = snapshot;
        };
        _engine.OnMediaOpened += () => Dispatcher.Invoke(() => MediaOpened?.Invoke(this, new RoutedEventArgs()));
        _engine.OnMediaEnded += () => Dispatcher.Invoke(() => MediaEnded?.Invoke(this, new RoutedEventArgs()));
        _engine.OnMediaFailed += message => Dispatcher.Invoke(() => MediaFailed?.Invoke(message));
        _engine.OnTracksChanged += tracks => Dispatcher.Invoke(() => TracksChanged?.Invoke(tracks));
        _engine.OnRuntimeInstallProgress += progress => Dispatcher.Invoke(() => RuntimeInstallProgress?.Invoke(progress));
    }

    public Uri? Source
    {
        get => _source;
        set
        {
            _source = value;
            _pendingPath = value?.LocalPath;
            if (_initialized && !string.IsNullOrWhiteSpace(_pendingPath))
            {
                _ = _engine.LoadAsync(_pendingPath, _disposeCts.Token);
            }
        }
    }

    public TimeSpan Position
    {
        get => _snapshot.Position;
        set
        {
            if (_initialized)
            {
                _ = _engine.SeekAsync(value, _disposeCts.Token);
            }
        }
    }

    public Duration NaturalDuration => _snapshot.Duration > TimeSpan.Zero ? new Duration(_snapshot.Duration) : Duration.Automatic;

    public double Volume
    {
        get => _snapshot.Volume > 0 ? _snapshot.Volume : _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_initialized)
            {
                _ = _engine.SetVolumeAsync(_volume, _disposeCts.Token);
            }
        }
    }

    public double PlaybackRate => _snapshot.Speed;
    public bool IsMuted => _snapshot.IsMuted;
    public bool IsPaused => _snapshot.IsPaused;
    public IReadOnlyList<MediaTrackInfo> CurrentTracks => _engine.CurrentTracks;
    public string ActiveHardwareDecoder => _snapshot.ActiveHardwareDecoder;
    public HardwareDecodingMode HardwareDecodingMode { get; set; } = HardwareDecodingMode.AutoSafe;

    public void Play() => _ = _engine.PlayAsync(_disposeCts.Token);
    public void Pause() => _ = _engine.PauseAsync(_disposeCts.Token);
    public void Stop() => _ = _engine.StopAsync(_disposeCts.Token);
    public void SeekBy(TimeSpan delta) => _ = _engine.SeekRelativeAsync(delta, _disposeCts.Token);
    public void StepFrame(bool forward) => _ = _engine.StepFrameAsync(forward, _disposeCts.Token);
    public void SetPlaybackRate(double speed) => _ = _engine.SetPlaybackRateAsync(speed, _disposeCts.Token);
    public void SetMute(bool muted) => _ = _engine.SetMuteAsync(muted, _disposeCts.Token);
    public void SelectAudioTrack(int? trackId) => _ = _engine.SetAudioTrackAsync(trackId, _disposeCts.Token);
    public void SelectSubtitleTrack(int? trackId) => _ = _engine.SetSubtitleTrackAsync(trackId, _disposeCts.Token);
    public void SetAudioDelay(double seconds) => _ = _engine.SetAudioDelayAsync(seconds, _disposeCts.Token);
    public void SetSubtitleDelay(double seconds) => _ = _engine.SetSubtitleDelayAsync(seconds, _disposeCts.Token);
    public void SetAspectRatio(string aspectRatio) => _ = _engine.SetAspectRatioAsync(aspectRatio, _disposeCts.Token);
    public void SetHardwareDecodingMode(HardwareDecodingMode mode)
    {
        HardwareDecodingMode = mode;
        if (_initialized)
        {
            _ = _engine.SetHardwareDecodingModeAsync(mode, _disposeCts.Token);
        }
    }
    public void SetZoom(double zoom) => _ = _engine.SetZoomAsync(zoom, _disposeCts.Token);
    public void SetPan(double x, double y) => _ = _engine.SetPanAsync(x, y, _disposeCts.Token);
    public void Screenshot(string outputPath) => _ = _engine.ScreenshotAsync(outputPath, _disposeCts.Token);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "static",
            string.Empty,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN,
            0,
            0,
            Math.Max((int)ActualWidth, 1),
            Math.Max((int)ActualHeight, 1),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to create the native video host window.");
        }

        _hwnd = hwnd;

        _initialized = true;
        _ = _engine.InitializeAsync(hwnd, HardwareDecodingMode, _disposeCts.Token)
            .ContinueWith(async task =>
            {
                if (task.IsFaulted)
                {
                    Dispatcher.Invoke(() => MediaFailed?.Invoke(task.Exception?.GetBaseException().Message ?? "mpv initialization failed."));
                    return;
                }

                await _engine.SetVolumeAsync(_volume, _disposeCts.Token);
                if (!string.IsNullOrWhiteSpace(_pendingPath))
                {
                    await _engine.LoadAsync(_pendingPath, _disposeCts.Token);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
            .Unwrap();

        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _disposeCts.Cancel();
        _ = _engine.DisposeAsync();
        NativeMethods.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.MoveWindow(_hwnd, 0, 0, Math.Max((int)rcBoundingBox.Width, 1), Math.Max((int)rcBoundingBox.Height, 1), true);
        }
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
    }

    private static class NativeMethods
    {
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_CLIPCHILDREN = 0x02000000;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parentHandle, IntPtr menuHandle, IntPtr instanceHandle, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);
    }
}

internal sealed class MpvPlaybackEngine : IPlaybackEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
    private readonly List<MediaTrackInfo> _tracks = [];
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _requestId;
    private int? _selectedAudioTrackId;
    private int? _selectedSubtitleTrackId;

    public event Action<PlaybackStateSnapshot>? OnStateChanged;
    public event Action<IReadOnlyList<MediaTrackInfo>>? OnTracksChanged;
    public event Action? OnMediaOpened;
    public event Action? OnMediaEnded;
    public event Action<string>? OnMediaFailed;
    public event Action<RuntimeInstallProgress>? OnRuntimeInstallProgress;

    public PlaybackStateSnapshot Snapshot { get; private set; } = new() { Volume = 0.8, IsPaused = true };
    public IReadOnlyList<MediaTrackInfo> CurrentTracks => _tracks.ToList();

    public async Task InitializeAsync(nint hostHandle, HardwareDecodingMode hardwareDecodingMode, CancellationToken cancellationToken)
    {
        if (_process is not null && !_process.HasExited)
        {
            return;
        }

        var mpvExePath = await MpvRuntimeInstaller.InstallAsync(progress => OnRuntimeInstallProgress?.Invoke(progress), cancellationToken);
        var pipeName = $"babelplayer-mpv-{Guid.NewGuid():N}";
        var pipePath = $"\\.\\pipe\\{pipeName}";

        var arguments = new StringBuilder()
            .Append("--idle=yes ")
            .Append("--force-window=yes ")
            .Append("--keep-open=yes ")
            .Append("--config=no ")
            .Append("--osc=no ")
            .Append("--terminal=no ")
            .Append("--input-terminal=no ")
            .Append("--sub-auto=no ")
            .Append("--sid=no ")
            .Append("--msg-level=all=warn ")
            .Append($"--wid={hostHandle} ")
            .Append($"--hwdec={MapHardwareDecodingMode(hardwareDecodingMode)} ")
            .Append($"--input-ipc-server=\\\\.\\pipe\\{pipeName}");

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = mpvExePath,
            Arguments = arguments.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Unable to start mpv.");

        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var started = Stopwatch.StartNew();
        while (!_pipe.IsConnected && started.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                await _pipe.ConnectAsync(1000, cancellationToken);
            }
            catch (TimeoutException)
            {
            }
        }

        if (!_pipe.IsConnected)
        {
            throw new InvalidOperationException("Unable to connect to the embedded mpv instance.");
        }

        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token), _readerCts.Token);

        await ObservePropertyAsync("time-pos", cancellationToken);
        await ObservePropertyAsync("duration", cancellationToken);
        await ObservePropertyAsync("pause", cancellationToken);
        await ObservePropertyAsync("volume", cancellationToken);
        await ObservePropertyAsync("mute", cancellationToken);
        await ObservePropertyAsync("speed", cancellationToken);
        await ObservePropertyAsync("track-list", cancellationToken);
        await ObservePropertyAsync("aid", cancellationToken);
        await ObservePropertyAsync("sid", cancellationToken);
        await ObservePropertyAsync("hwdec-current", cancellationToken);
    }

    public Task LoadAsync(string path, CancellationToken cancellationToken) => SendCommandAsync(cancellationToken, "loadfile", path, "replace");
    public Task PlayAsync(CancellationToken cancellationToken) => SetPropertyAsync("pause", false, cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken) => SetPropertyAsync("pause", true, cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => SendCommandAsync(cancellationToken, "stop");
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) => SendCommandAsync(cancellationToken, "seek", position.TotalSeconds, "absolute");
    public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken) => SendCommandAsync(cancellationToken, "seek", delta.TotalSeconds, "relative");
    public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken) => SetPropertyAsync("speed", Math.Clamp(speed, 0.25, 2.0), cancellationToken);
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => SetPropertyAsync("volume", Math.Round(Math.Clamp(volume, 0, 1) * 100, 1), cancellationToken);
    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken) => SetPropertyAsync("mute", muted, cancellationToken);
    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken) => SendCommandAsync(cancellationToken, forward ? "frame-step" : "frame-back-step");
    public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken) => SetPropertyAsync("aid", trackId is null ? "no" : trackId.Value, cancellationToken);
    public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken) => SetPropertyAsync("sid", trackId is null ? "no" : trackId.Value, cancellationToken);
    public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken) => SetPropertyAsync("audio-delay", seconds, cancellationToken);
    public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken) => SetPropertyAsync("sub-delay", seconds, cancellationToken);
    public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken) => SetPropertyAsync("video-aspect-override", string.IsNullOrWhiteSpace(aspectRatio) || string.Equals(aspectRatio, "auto", StringComparison.OrdinalIgnoreCase) ? "no" : aspectRatio, cancellationToken);
    public Task SetHardwareDecodingModeAsync(HardwareDecodingMode mode, CancellationToken cancellationToken) => SetPropertyAsync("hwdec", MapHardwareDecodingMode(mode), cancellationToken);
    public Task SetZoomAsync(double zoom, CancellationToken cancellationToken) => SetPropertyAsync("video-zoom", zoom, cancellationToken);
    public async Task SetPanAsync(double x, double y, CancellationToken cancellationToken)
    {
        await SetPropertyAsync("video-pan-x", x, cancellationToken);
        await SetPropertyAsync("video-pan-y", y, cancellationToken);
    }
    public Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken) => SendCommandAsync(cancellationToken, "screenshot-to-file", outputPath, "video");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_readerCts is not null)
            {
                await _readerCts.CancelAsync();
            }
        }
        catch
        {
        }

        try
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _pipe?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            _process?.Dispose();
        }
    }

    private async Task ObservePropertyAsync(string propertyName, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        await WriteMessageAsync(new { command = new object[] { "observe_property", id, propertyName }, request_id = id }, cancellationToken);
    }

    private Task SetPropertyAsync(string propertyName, object value, CancellationToken cancellationToken)
    {
        return SendCommandAsync(cancellationToken, "set_property", propertyName, value);
    }

    private async Task SendCommandAsync(CancellationToken cancellationToken, params object[] command)
    {
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = completion;
        await WriteMessageAsync(new { command, request_id = id }, cancellationToken);
        var response = await completion.Task.WaitAsync(cancellationToken);
        if (response is JsonElement element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty("error", out var errorProperty))
        {
            var error = errorProperty.GetString();
            if (!string.Equals(error, "success", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"mpv command failed: {error}.");
            }
        }
    }

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("The embedded mpv instance is not ready.");
        }

        var json = JsonSerializer.Serialize(payload);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _reader is not null)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnMediaFailed?.Invoke(ex.Message);
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("request_id", out var requestIdProperty))
            {
                var requestId = requestIdProperty.GetInt32();
                if (_pendingRequests.TryRemove(requestId, out var completion))
                {
                    completion.TrySetResult(root.Clone());
                }
            }

            if (root.TryGetProperty("event", out var eventProperty))
            {
                var eventName = eventProperty.GetString();
                switch (eventName)
                {
                    case "property-change":
                        HandlePropertyChange(root);
                        break;
                    case "file-loaded":
                        OnMediaOpened?.Invoke();
                        break;
                    case "end-file":
                        var reason = root.TryGetProperty("reason", out var reasonProperty) ? reasonProperty.GetString() : string.Empty;
                        if (string.Equals(reason, "eof", StringComparison.OrdinalIgnoreCase))
                        {
                            OnMediaEnded?.Invoke();
                        }
                        break;
                }
            }
        }
    }

    private void HandlePropertyChange(JsonElement root)
    {
        if (!root.TryGetProperty("name", out var nameProperty))
        {
            return;
        }

        var name = nameProperty.GetString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var data = root.TryGetProperty("data", out var dataElement) ? dataElement : default;
        var snapshot = Snapshot;
        switch (name)
        {
            case "time-pos":
                snapshot = snapshot with { Position = ReadSeconds(data) };
                break;
            case "duration":
                snapshot = snapshot with { Duration = ReadSeconds(data) };
                break;
            case "pause":
                snapshot = snapshot with { IsPaused = data.ValueKind == JsonValueKind.True };
                break;
            case "volume":
                snapshot = snapshot with { Volume = data.ValueKind is JsonValueKind.Number ? Math.Clamp(data.GetDouble() / 100.0, 0, 1) : snapshot.Volume };
                break;
            case "mute":
                snapshot = snapshot with { IsMuted = data.ValueKind == JsonValueKind.True };
                break;
            case "speed":
                snapshot = snapshot with { Speed = data.ValueKind is JsonValueKind.Number ? data.GetDouble() : snapshot.Speed };
                break;
            case "hwdec-current":
                snapshot = snapshot with { ActiveHardwareDecoder = data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : string.Empty };
                break;
            case "aid":
                _selectedAudioTrackId = data.ValueKind == JsonValueKind.Number ? data.GetInt32() : null;
                UpdateTrackSelection();
                return;
            case "sid":
                _selectedSubtitleTrackId = data.ValueKind == JsonValueKind.Number ? data.GetInt32() : null;
                UpdateTrackSelection();
                return;
            case "track-list":
                ParseTracks(data);
                return;
        }

        Snapshot = snapshot;
        OnStateChanged?.Invoke(Snapshot);
    }

    private void ParseTracks(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        _tracks.Clear();
        foreach (var trackElement in data.EnumerateArray())
        {
            if (!trackElement.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var type = trackElement.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : string.Empty;
            var kind = type switch
            {
                "audio" => MediaTrackKind.Audio,
                "sub" => MediaTrackKind.Subtitle,
                _ => MediaTrackKind.Video
            };

            var codec = trackElement.TryGetProperty("codec", out var codecProperty) ? codecProperty.GetString() ?? string.Empty : string.Empty;
            var title = trackElement.TryGetProperty("title", out var titleProperty) ? titleProperty.GetString() ?? string.Empty : string.Empty;
            var lang = trackElement.TryGetProperty("lang", out var langProperty) ? langProperty.GetString() ?? "und" : "und";
            var ffIndex = trackElement.TryGetProperty("ff-index", out var ffIndexProperty) && ffIndexProperty.ValueKind == JsonValueKind.Number
                ? ffIndexProperty.GetInt32()
                : (int?)null;
            var isSelected = kind switch
            {
                MediaTrackKind.Audio => _selectedAudioTrackId == idProperty.GetInt32() || trackElement.TryGetProperty("selected", out var audioSelected) && audioSelected.ValueKind == JsonValueKind.True,
                MediaTrackKind.Subtitle => _selectedSubtitleTrackId == idProperty.GetInt32() || trackElement.TryGetProperty("selected", out var subtitleSelected) && subtitleSelected.ValueKind == JsonValueKind.True,
                _ => trackElement.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True
            };

            _tracks.Add(new MediaTrackInfo
            {
                Id = idProperty.GetInt32(),
                FfIndex = ffIndex,
                Kind = kind,
                Title = title,
                Language = string.IsNullOrWhiteSpace(lang) ? "und" : lang,
                Codec = codec,
                IsEmbedded = true,
                IsSelected = isSelected,
                IsTextBased = kind != MediaTrackKind.Subtitle || IsTextSubtitleCodec(codec)
            });
        }

        UpdateTrackSelection();
        OnTracksChanged?.Invoke(CurrentTracks);
    }

    private void UpdateTrackSelection()
    {
        if (_tracks.Count == 0)
        {
            return;
        }

        for (var index = 0; index < _tracks.Count; index++)
        {
            var track = _tracks[index];
            var isSelected = track.Kind switch
            {
                MediaTrackKind.Audio => _selectedAudioTrackId == track.Id,
                MediaTrackKind.Subtitle => _selectedSubtitleTrackId == track.Id,
                _ => track.IsSelected
            };
            _tracks[index] = new MediaTrackInfo
            {
                Id = track.Id,
                FfIndex = track.FfIndex,
                Kind = track.Kind,
                Title = track.Title,
                Language = track.Language,
                Codec = track.Codec,
                IsEmbedded = track.IsEmbedded,
                IsSelected = isSelected,
                IsTextBased = track.IsTextBased
            };
        }

        OnTracksChanged?.Invoke(CurrentTracks);
    }

    private static bool IsTextSubtitleCodec(string codec)
    {
        return codec.Contains("subrip", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("mov_text", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("ass", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("ssa", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("webvtt", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("text", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan ReadSeconds(JsonElement data)
    {
        return data.ValueKind is JsonValueKind.Number
            ? TimeSpan.FromSeconds(data.GetDouble())
            : TimeSpan.Zero;
    }

    private static string MapHardwareDecodingMode(HardwareDecodingMode mode)
    {
        return mode switch
        {
            HardwareDecodingMode.D3D11 => "d3d11va",
            HardwareDecodingMode.Nvdec => "nvdec",
            HardwareDecodingMode.Software => "no",
            _ => "auto-safe"
        };
    }
}
