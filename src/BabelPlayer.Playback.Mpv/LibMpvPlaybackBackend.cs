using System.Globalization;
using System.Runtime.InteropServices;
using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.Playback.Mpv;

public sealed class LibMpvPlaybackBackend : IPlaybackBackend
{
    private readonly object _sync = new();
    private readonly List<MediaTrackInfo> _tracks = [];
    private readonly BackendPlaybackClock _clock = new();
    private CancellationTokenSource? _eventLoopCts;
    private Task? _eventLoopTask;
    private IntPtr _context;
    private IntPtr _hostHandle;
    private bool _initialized;
    private PlaybackBackendState _state = new() { Volume = 0.8 };
    private ClockSnapshot _clockSnapshot = new(TimeSpan.Zero, TimeSpan.Zero, 1.0, true, false, DateTimeOffset.UtcNow);
    private HardwareDecodingMode _hardwareDecodingMode = HardwareDecodingMode.AutoSafe;
    private int? _selectedAudioTrackId;
    private int? _selectedSubtitleTrackId;

    public event Action<PlaybackBackendState>? StateChanged;
    public event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<string>? MediaFailed;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgress;

    public IPlaybackClock Clock => _clock;
    public PlaybackBackendState State { get { lock (_sync) { return _state; } } }
    public IReadOnlyList<MediaTrackInfo> CurrentTracks { get { lock (_sync) { return _tracks.ToArray(); } } }
    public HardwareDecodingMode HardwareDecodingMode => _hardwareDecodingMode;

    public Task InitializeAsync(nint hostHandle, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hostHandle);

        lock (_sync)
        {
            if (_initialized)
            {
                return Task.CompletedTask;
            }

            _hostHandle = hostHandle;
            _context = LibMpvNative.mpv_create();
            if (_context == IntPtr.Zero)
            {
                throw new InvalidOperationException("mpv_create returned a null context.");
            }

            SetOptionString("config", "no");
            SetOptionString("terminal", "no");
            SetOptionString("msg-level", "all=warn");
            SetOptionString("input-default-bindings", "no");
            SetOptionString("input-vo-keyboard", "no");
            SetOptionString("osc", "no");
            SetOptionString("sub-auto", "no");
            SetOptionString("sid", "no");
            SetOptionString("idle", "yes");
            SetOptionString("keep-open", "yes");
            SetOptionString("force-window", "yes");
            SetOptionString("wid", hostHandle.ToString(CultureInfo.InvariantCulture));
            SetOptionString("hwdec", MapHardwareDecodingMode(_hardwareDecodingMode));

            VerifyMpvResult(LibMpvNative.mpv_initialize(_context), "mpv_initialize");

            ObserveProperty(1, "time-pos", MpvFormat.Double);
            ObserveProperty(2, "duration", MpvFormat.Double);
            ObserveProperty(3, "pause", MpvFormat.Flag);
            ObserveProperty(4, "volume", MpvFormat.Double);
            ObserveProperty(5, "mute", MpvFormat.Flag);
            ObserveProperty(6, "speed", MpvFormat.Double);
            ObserveProperty(7, "seekable", MpvFormat.Flag);
            ObserveProperty(8, "aid", MpvFormat.Int64);
            ObserveProperty(9, "sid", MpvFormat.Int64);
            ObserveProperty(10, "track-list", MpvFormat.None);
            ObserveProperty(11, "video-params", MpvFormat.None);
            ObserveProperty(12, "video-out-params", MpvFormat.None);
            ObserveProperty(13, "hwdec-current", MpvFormat.None);

            _eventLoopCts = new CancellationTokenSource();
            _eventLoopTask = Task.Run(() => EventLoopAsync(_eventLoopCts.Token), CancellationToken.None);
            _initialized = true;
        }

        RefreshDerivedState();
        RefreshTrackList();
        return Task.CompletedTask;
    }

    public Task LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            EnsureInitialized();
            _state = _state with
            {
                Path = path,
                HasAudio = false,
                HasVideo = false,
                VideoWidth = 0,
                VideoHeight = 0,
                VideoDisplayWidth = 0,
                VideoDisplayHeight = 0
            };
            _clockSnapshot = _clockSnapshot with
            {
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero,
                SampledAtUtc = DateTimeOffset.UtcNow
            };
        }

        StateChanged?.Invoke(State);
        _clock.Update(CurrentClock());
        ExecuteCommand("loadfile", path, "replace");
        return Task.CompletedTask;
    }

    public Task PlayAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "pause", "no"); return Task.CompletedTask; }
    public Task PauseAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "pause", "yes"); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("stop"); return Task.CompletedTask; }
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("seek", position.TotalSeconds.ToString(CultureInfo.InvariantCulture), "absolute"); return Task.CompletedTask; }
    public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("seek", delta.TotalSeconds.ToString(CultureInfo.InvariantCulture), "relative"); return Task.CompletedTask; }
    public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "speed", Math.Clamp(speed, 0.25, 2.0).ToString(CultureInfo.InvariantCulture)); return Task.CompletedTask; }
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "volume", Math.Round(Math.Clamp(volume, 0, 1) * 100, 1).ToString(CultureInfo.InvariantCulture)); return Task.CompletedTask; }
    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "mute", muted ? "yes" : "no"); return Task.CompletedTask; }
    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand(forward ? "frame-step" : "frame-back-step"); return Task.CompletedTask; }
    public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "aid", trackId?.ToString(CultureInfo.InvariantCulture) ?? "no"); return Task.CompletedTask; }
    public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "sid", trackId?.ToString(CultureInfo.InvariantCulture) ?? "no"); return Task.CompletedTask; }
    public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "audio-delay", seconds.ToString(CultureInfo.InvariantCulture)); return Task.CompletedTask; }
    public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "sub-delay", seconds.ToString(CultureInfo.InvariantCulture)); return Task.CompletedTask; }
    public Task SetZoomAsync(double zoom, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("set", "video-zoom", zoom.ToString(CultureInfo.InvariantCulture)); return Task.CompletedTask; }
    public Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ExecuteCommand("screenshot-to-file", outputPath, "video"); return Task.CompletedTask; }

    public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteCommand("set", "video-aspect-override", string.IsNullOrWhiteSpace(aspectRatio) || string.Equals(aspectRatio, "auto", StringComparison.OrdinalIgnoreCase) ? "no" : aspectRatio);
        return Task.CompletedTask;
    }

    public Task SetHardwareDecodingModeAsync(HardwareDecodingMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _hardwareDecodingMode = mode;
        ExecuteCommand("set", "hwdec", MapHardwareDecodingMode(mode));
        return Task.CompletedTask;
    }

    public Task SetPanAsync(double x, double y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteCommand("set", "video-pan-x", x.ToString(CultureInfo.InvariantCulture));
        ExecuteCommand("set", "video-pan-y", y.ToString(CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        Task? eventLoopTask;
        IntPtr context;

        lock (_sync)
        {
            cts = _eventLoopCts;
            eventLoopTask = _eventLoopTask;
            context = _context;
            _eventLoopCts = null;
            _eventLoopTask = null;
            _context = IntPtr.Zero;
            _initialized = false;
        }

        if (cts is not null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        if (eventLoopTask is not null)
        {
            try { await eventLoopTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        if (context != IntPtr.Zero)
        {
            LibMpvNative.mpv_terminate_destroy(context);
        }
    }

    private void EventLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IntPtr context;
            lock (_sync) { context = _context; }
            if (context == IntPtr.Zero) { break; }

            var eventPtr = LibMpvNative.mpv_wait_event(context, 0.1);
            if (eventPtr == IntPtr.Zero) { continue; }

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPtr);
            switch (mpvEvent.EventId)
            {
                case MpvEventId.None:
                    continue;
                case MpvEventId.PropertyChange:
                    HandlePropertyChange(mpvEvent);
                    break;
                case MpvEventId.FileLoaded:
                    RefreshDerivedState();
                    RefreshTrackList();
                    MediaOpened?.Invoke();
                    break;
                case MpvEventId.EndFile:
                    HandleEndFile(mpvEvent);
                    break;
                case MpvEventId.Shutdown:
                    return;
            }
        }
    }

    private void HandlePropertyChange(MpvEvent mpvEvent)
    {
        if (mpvEvent.Data == IntPtr.Zero) { return; }
        var property = Marshal.PtrToStructure<MpvEventProperty>(mpvEvent.Data);
        var propertyName = Marshal.PtrToStringUTF8(property.Name);
        if (string.IsNullOrWhiteSpace(propertyName)) { return; }

        switch (propertyName)
        {
            case "time-pos" when property.Format == MpvFormat.Double && property.Data != IntPtr.Zero:
                UpdateClock(position: TimeSpan.FromSeconds(Marshal.PtrToStructure<double>(property.Data)));
                break;
            case "duration" when property.Format == MpvFormat.Double && property.Data != IntPtr.Zero:
                UpdateClock(duration: TimeSpan.FromSeconds(Marshal.PtrToStructure<double>(property.Data)));
                break;
            case "pause" when property.Format == MpvFormat.Flag && property.Data != IntPtr.Zero:
                UpdateClock(isPaused: Marshal.PtrToStructure<int>(property.Data) != 0);
                break;
            case "volume" when property.Format == MpvFormat.Double && property.Data != IntPtr.Zero:
                UpdateState(volume: Math.Clamp(Marshal.PtrToStructure<double>(property.Data) / 100d, 0d, 1d));
                break;
            case "mute" when property.Format == MpvFormat.Flag && property.Data != IntPtr.Zero:
                UpdateState(isMuted: Marshal.PtrToStructure<int>(property.Data) != 0);
                break;
            case "speed" when property.Format == MpvFormat.Double && property.Data != IntPtr.Zero:
                UpdateClock(rate: Marshal.PtrToStructure<double>(property.Data));
                break;
            case "seekable" when property.Format == MpvFormat.Flag && property.Data != IntPtr.Zero:
                UpdateClock(isSeekable: Marshal.PtrToStructure<int>(property.Data) != 0);
                break;
            case "aid":
                _selectedAudioTrackId = property.Format == MpvFormat.Int64 && property.Data != IntPtr.Zero ? checked((int)Marshal.PtrToStructure<long>(property.Data)) : null;
                RefreshTrackList();
                break;
            case "sid":
                _selectedSubtitleTrackId = property.Format == MpvFormat.Int64 && property.Data != IntPtr.Zero ? checked((int)Marshal.PtrToStructure<long>(property.Data)) : null;
                RefreshTrackList();
                break;
            case "track-list":
                RefreshTrackList();
                break;
            case "video-params":
            case "video-out-params":
            case "hwdec-current":
                RefreshDerivedState();
                break;
        }
    }

    private void HandleEndFile(MpvEvent mpvEvent)
    {
        if (mpvEvent.Data == IntPtr.Zero) { return; }
        var endFile = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
        switch (endFile.Reason)
        {
            case MpvEndFileReason.Eof:
                MediaEnded?.Invoke();
                break;
            case MpvEndFileReason.Error:
                MediaFailed?.Invoke($"mpv failed with error code {endFile.Error}.");
                break;
        }
    }

    private void RefreshDerivedState()
    {
        MpvNode videoParams;
        MpvNode videoOutParams;
        bool? muted;
        double? volume;
        string? hwdecCurrent;

        lock (_sync)
        {
            EnsureInitialized();
            UpdateClockInternal(GetTimeProperty("time-pos"), GetTimeProperty("duration"), GetDoubleProperty("speed"), GetFlagProperty("pause"), GetFlagProperty("seekable"));
            videoParams = GetNodeProperty("video-params");
            videoOutParams = GetNodeProperty("video-out-params");
            muted = GetFlagProperty("mute");
            volume = GetDoubleProperty("volume");
            hwdecCurrent = GetStringProperty("hwdec-current");

            var width = LibMpvNodeHelpers.GetNodeMapInt(videoParams, "w");
            var height = LibMpvNodeHelpers.GetNodeMapInt(videoParams, "h");
            var displayWidth = LibMpvNodeHelpers.GetNodeMapInt(videoOutParams, "dw") ?? LibMpvNodeHelpers.GetNodeMapInt(videoOutParams, "w") ?? width ?? 0;
            var displayHeight = LibMpvNodeHelpers.GetNodeMapInt(videoOutParams, "dh") ?? LibMpvNodeHelpers.GetNodeMapInt(videoOutParams, "h") ?? height ?? 0;

            _state = _state with
            {
                HasVideo = (width ?? 0) > 0 && (height ?? 0) > 0,
                HasAudio = _tracks.Any(track => track.Kind == MediaTrackKind.Audio),
                VideoWidth = width ?? 0,
                VideoHeight = height ?? 0,
                VideoDisplayWidth = displayWidth,
                VideoDisplayHeight = displayHeight,
                IsMuted = muted ?? _state.IsMuted,
                Volume = Math.Clamp((volume ?? (_state.Volume * 100d)) / 100d, 0d, 1d),
                ActiveHardwareDecoder = hwdecCurrent ?? string.Empty
            };
        }

        LibMpvNative.mpv_free_node_contents(ref videoParams);
        LibMpvNative.mpv_free_node_contents(ref videoOutParams);
        StateChanged?.Invoke(State);
        _clock.Update(CurrentClock());
    }

    private void RefreshTrackList()
    {
        MpvNode node;
        int? selectedAudio;
        int? selectedSubtitle;
        lock (_sync)
        {
            EnsureInitialized();
            node = GetNodeProperty("track-list");
            selectedAudio = GetIntProperty("aid");
            selectedSubtitle = GetIntProperty("sid");
            _selectedAudioTrackId = selectedAudio;
            _selectedSubtitleTrackId = selectedSubtitle;
        }

        var parsedTracks = LibMpvNodeHelpers.ParseTracks(node, selectedAudio, selectedSubtitle);
        LibMpvNative.mpv_free_node_contents(ref node);

        lock (_sync)
        {
            _tracks.Clear();
            _tracks.AddRange(parsedTracks);
            _state = _state with { HasAudio = _tracks.Any(track => track.Kind == MediaTrackKind.Audio) };
        }

        TracksChanged?.Invoke(CurrentTracks);
        StateChanged?.Invoke(State);
    }

    private void UpdateClock(TimeSpan? position = null, TimeSpan? duration = null, double? rate = null, bool? isPaused = null, bool? isSeekable = null)
    {
        lock (_sync)
        {
            UpdateClockInternal(position, duration, rate, isPaused, isSeekable);
        }

        _clock.Update(CurrentClock());
    }

    private void UpdateClockInternal(TimeSpan? position, TimeSpan? duration, double? rate, bool? isPaused, bool? isSeekable)
    {
        _clockSnapshot = _clockSnapshot with
        {
            Position = position ?? _clockSnapshot.Position,
            Duration = duration ?? _clockSnapshot.Duration,
            Rate = rate ?? _clockSnapshot.Rate,
            IsPaused = isPaused ?? _clockSnapshot.IsPaused,
            IsSeekable = isSeekable ?? _clockSnapshot.IsSeekable,
            SampledAtUtc = DateTimeOffset.UtcNow
        };
    }

    private void UpdateState(bool? isMuted = null, double? volume = null)
    {
        lock (_sync)
        {
            _state = _state with { IsMuted = isMuted ?? _state.IsMuted, Volume = volume ?? _state.Volume };
        }

        StateChanged?.Invoke(State);
    }

    private ClockSnapshot CurrentClock() { lock (_sync) { return _clockSnapshot; } }

    private void ExecuteCommand(params string[] args)
    {
        IntPtr context;
        lock (_sync) { EnsureInitialized(); context = _context; }
        VerifyMpvResult(LibMpvNative.mpv_command(context, args), $"mpv_command({string.Join(", ", args)})");
    }

    private void SetOptionString(string name, string value) => VerifyMpvResult(LibMpvNative.mpv_set_option_string(_context, name, value), $"mpv_set_option_string({name})");
    private void ObserveProperty(ulong replyUserData, string name, MpvFormat format) => VerifyMpvResult(LibMpvNative.mpv_observe_property(_context, replyUserData, name, format), $"mpv_observe_property({name})");
    private TimeSpan GetTimeProperty(string propertyName) => GetDoubleProperty(propertyName) is { } value ? TimeSpan.FromSeconds(value) : TimeSpan.Zero;
    private double? GetDoubleProperty(string propertyName) => LibMpvNative.mpv_get_property_double(_context, propertyName, MpvFormat.Double, out var value) >= 0 ? value : null;
    private bool? GetFlagProperty(string propertyName) => LibMpvNative.mpv_get_property_flag(_context, propertyName, MpvFormat.Flag, out var value) >= 0 ? value != 0 : null;
    private int? GetIntProperty(string propertyName) => LibMpvNative.mpv_get_property_int64(_context, propertyName, MpvFormat.Int64, out var value) >= 0 ? checked((int)value) : null;
    private MpvNode GetNodeProperty(string propertyName) => LibMpvNative.mpv_get_property_node(_context, propertyName, MpvFormat.Node, out var node) >= 0 ? node : default;

    private string? GetStringProperty(string propertyName)
    {
        if (LibMpvNative.mpv_get_property_string(_context, propertyName, MpvFormat.String, out var stringPtr) < 0 || stringPtr == IntPtr.Zero)
        {
            return null;
        }

        try { return Marshal.PtrToStringUTF8(stringPtr); }
        finally { LibMpvNative.mpv_free(stringPtr); }
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _context == IntPtr.Zero || _hostHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("libmpv playback backend has not been initialized.");
        }
    }

    private static void VerifyMpvResult(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"{operation} failed with mpv error code {result}.");
        }
    }

    private static string MapHardwareDecodingMode(HardwareDecodingMode mode) => mode switch
    {
        HardwareDecodingMode.D3D11 => "d3d11va",
        HardwareDecodingMode.Nvdec => "nvdec",
        HardwareDecodingMode.Software => "no",
        _ => "auto-safe"
    };

    private sealed class BackendPlaybackClock : IPlaybackClock
    {
        private ClockSnapshot _current = new(TimeSpan.Zero, TimeSpan.Zero, 1.0, true, false, DateTimeOffset.UtcNow);

        public event Action<ClockSnapshot>? Changed;

        public ClockSnapshot Current => _current;

        public void Update(ClockSnapshot snapshot)
        {
            _current = snapshot;
            Changed?.Invoke(snapshot);
        }
    }
}
