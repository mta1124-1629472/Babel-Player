namespace BabelPlayer.App;

public sealed class TransportViewModel : ObservableObject
{
    private string _currentTimeText = "00:00";
    private string _durationText = "00:00";
    private double _positionSeconds;
    private double _durationSeconds;
    private double _volume = 0.8;
    private double _playbackRate = 1.0;
    private bool _isMuted;
    private bool _isPaused = true;

    public string CurrentTimeText
    {
        get => _currentTimeText;
        set => SetProperty(ref _currentTimeText, value);
    }

    public string DurationText
    {
        get => _durationText;
        set => SetProperty(ref _durationText, value);
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set => SetProperty(ref _positionSeconds, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set => SetProperty(ref _durationSeconds, value);
    }

    public double Volume
    {
        get => _volume;
        set => SetProperty(ref _volume, value);
    }

    public double PlaybackRate
    {
        get => _playbackRate;
        set => SetProperty(ref _playbackRate, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }
}
