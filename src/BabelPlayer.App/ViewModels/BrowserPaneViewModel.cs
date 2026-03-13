using System.Collections.ObjectModel;

namespace BabelPlayer.App;

public sealed class BrowserPaneViewModel : ObservableObject
{
    private bool _isVisible = true;

    public ObservableCollection<LibraryEntrySnapshot> Roots { get; } = [];

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}
