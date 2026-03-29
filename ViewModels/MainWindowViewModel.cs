using Babel.Deck.Services;

namespace Babel.Deck.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(SessionWorkflowCoordinator coordinator)
    {
        Coordinator = coordinator;
        Playback = new EmbeddedPlaybackViewModel(coordinator);
        Inspection = new SegmentInspectionViewModel(Playback);
    }

    public SessionWorkflowCoordinator Coordinator { get; }

    public EmbeddedPlaybackViewModel Playback { get; }

    public SegmentInspectionViewModel Inspection { get; }
}
