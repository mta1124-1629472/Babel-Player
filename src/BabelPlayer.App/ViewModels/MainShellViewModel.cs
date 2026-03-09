using System.Collections.ObjectModel;
using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class MainShellViewModel : ObservableObject
{
    private string _windowTitle = "Babel Player";
    private string _statusMessage = "Open media to begin.";
    private bool _isStatusOpen = true;
    private bool _isDarkTheme = true;
    private string _activeHardwareDecoder = string.Empty;
    private AppPlayerSettings _settings = new();

    public BrowserPaneViewModel Browser { get; } = new();
    public PlaylistViewModel Playlist { get; } = new();
    public TransportViewModel Transport { get; } = new();
    public SubtitleOverlayViewModel SubtitleOverlay { get; } = new();

    public ObservableCollection<string> StatusFeed { get; } = [];

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetProperty(ref _windowTitle, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsStatusOpen
    {
        get => _isStatusOpen;
        set => SetProperty(ref _isStatusOpen, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => SetProperty(ref _isDarkTheme, value);
    }

    public string ActiveHardwareDecoder
    {
        get => _activeHardwareDecoder;
        set => SetProperty(ref _activeHardwareDecoder, value);
    }

    public AppPlayerSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }
}
