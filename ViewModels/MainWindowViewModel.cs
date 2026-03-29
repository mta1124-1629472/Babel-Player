using Babel.Deck.Services;

namespace Babel.Deck.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(SessionWorkflowCoordinator coordinator)
    {
        Coordinator = coordinator;
        Playback = new EmbeddedPlaybackViewModel(coordinator);
    }

    public SessionWorkflowCoordinator Coordinator { get; }

    public EmbeddedPlaybackViewModel Playback { get; }
}
