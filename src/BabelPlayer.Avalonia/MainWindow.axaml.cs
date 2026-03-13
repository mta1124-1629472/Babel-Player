using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BabelPlayer.Avalonia;

public partial class MainWindow : Window
{
    private MpvNativeHost? _videoHost;
    private Button? _playPauseButton;
    private TextBlock? _statusTextBlock;
    private Slider? _volumeSlider;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _videoHost ??= this.FindControl<MpvNativeHost>("VideoHost");
        _playPauseButton ??= this.FindControl<Button>("PlayPauseButton");
        _statusTextBlock ??= this.FindControl<TextBlock>("StatusTextBlock");
        _volumeSlider ??= this.FindControl<Slider>("VolumeSlider");

        if (_videoHost is not null)
        {
            _videoHost.StatusChanged -= HandleHostStatusChanged;
            _videoHost.StatusChanged += HandleHostStatusChanged;
            _videoHost.PlaybackFailed -= HandleHostPlaybackFailed;
            _videoHost.PlaybackFailed += HandleHostPlaybackFailed;
            _videoHost.SourcePath = GetTestVideoPath();
        }

        if (_volumeSlider is not null)
        {
            _volumeSlider.PropertyChanged -= HandleVolumeSliderPropertyChanged;
            _volumeSlider.PropertyChanged += HandleVolumeSliderPropertyChanged;
        }

        UpdateStatus(File.Exists(GetTestVideoPath())
            ? $"Loading {Path.GetFileName(GetTestVideoPath())}..."
            : $"Missing test video: {GetTestVideoPath()}");
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_videoHost is not null)
        {
            _videoHost.StatusChanged -= HandleHostStatusChanged;
            _videoHost.PlaybackFailed -= HandleHostPlaybackFailed;
        }

        if (_volumeSlider is not null)
        {
            _volumeSlider.PropertyChanged -= HandleVolumeSliderPropertyChanged;
        }

        base.OnClosed(e);
    }

    private void HandleHostStatusChanged(string message)
    {
        UpdateStatus(message);
        UpdatePlayPauseButton();
    }

    private void HandleHostPlaybackFailed(string message)
    {
        UpdateStatus(message);
    }

    private void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        _videoHost?.Load(GetTestVideoPath());
        UpdatePlayPauseButton();
    }

    private void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_videoHost is null)
        {
            return;
        }

        if (_videoHost.IsPaused)
        {
            _videoHost.Play();
        }
        else
        {
            _videoHost.Pause();
        }

        UpdatePlayPauseButton();
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        _videoHost?.Stop();
        UpdatePlayPauseButton();
    }

    private void SeekBackButton_Click(object? sender, RoutedEventArgs e)
    {
        _videoHost?.SeekRelative(TimeSpan.FromSeconds(-10));
    }

    private void SeekForwardButton_Click(object? sender, RoutedEventArgs e)
    {
        _videoHost?.SeekRelative(TimeSpan.FromSeconds(10));
    }

    private void HandleVolumeSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name != nameof(Slider.Value) || sender is not Slider slider)
        {
            return;
        }

        _videoHost?.SetVolume(slider.Value);
    }

    private void UpdateStatus(string message)
    {
        if (_statusTextBlock is not null)
        {
            _statusTextBlock.Text = message;
        }
    }

    private void UpdatePlayPauseButton()
    {
        if (_playPauseButton is not null)
        {
            _playPauseButton.Content = _videoHost?.IsPaused == false ? "Pause" : "Play";
        }
    }

    private static string GetTestVideoPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Assets", "test-video.mp4");
    }
}
