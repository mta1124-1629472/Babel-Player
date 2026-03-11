using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;

namespace BabelPlayer.WinUI;

public sealed class StageCoordinator
{
    private readonly FrameworkElement _stageRelativeTo;
    private readonly WinUIWindowModeService _windowModeService;
    private readonly IVideoPresenter _videoPresenter;
    private readonly ISubtitlePresenter _subtitlePresenter;
    private int _modalSuppressionCount;
    private bool _isWindowActive = true;
    private bool _isFullscreenOverlayVisible;

    public StageCoordinator(
        FrameworkElement stageRelativeTo,
        WinUIWindowModeService windowModeService,
        IVideoPresenter videoPresenter,
        ISubtitlePresenter subtitlePresenter)
    {
        _stageRelativeTo = stageRelativeTo;
        _windowModeService = windowModeService;
        _videoPresenter = videoPresenter;
        _subtitlePresenter = subtitlePresenter;
    }

    public void SetWindowActive(bool isActive)
    {
        _isWindowActive = isActive;
        if (!isActive)
        {
            _subtitlePresenter.Hide();
        }
    }

    public void SetFullscreenOverlayVisible(bool isVisible)
    {
        _isFullscreenOverlayVisible = isVisible;
    }

    public void RequestStageSync()
    {
        _videoPresenter.RequestBoundsSync();
    }

    public void HideSubtitlePresentation()
    {
        _subtitlePresenter.Hide();
    }

    public void PresentSubtitles(SubtitlePresentationModel model, SubtitleStyleSettings style, bool hasLoadedMedia)
    {
        if (_modalSuppressionCount > 0 || !_isWindowActive || !hasLoadedMedia || !model.IsVisible)
        {
            _subtitlePresenter.Hide();
            return;
        }

        var stageBounds = _videoPresenter.GetStageBounds(_stageRelativeTo);
        if (stageBounds.Width <= 0 || stageBounds.Height <= 0)
        {
            _subtitlePresenter.Hide();
            return;
        }

        _subtitlePresenter.ApplyStyle(style);
        _subtitlePresenter.Present(model, stageBounds, GetSubtitleBottomOffset(style));
    }

    public IDisposable SuppressModalUi()
    {
        _modalSuppressionCount++;
        _subtitlePresenter.Hide();
        return new ModalUiSuppressionScope(this);
    }

    private int GetSubtitleBottomOffset(SubtitleStyleSettings style)
    {
        var styleOffset = (int)Math.Round(style.BottomMargin);
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            return (_isFullscreenOverlayVisible ? 248 : 68) + styleOffset;
        }

        return 44 + styleOffset;
    }

    private void ReleaseModalUiSuppression()
    {
        if (_modalSuppressionCount == 0)
        {
            return;
        }

        _modalSuppressionCount--;
    }

    private sealed class ModalUiSuppressionScope : IDisposable
    {
        private StageCoordinator? _owner;

        public ModalUiSuppressionScope(StageCoordinator owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_owner is null)
            {
                return;
            }

            _owner.ReleaseModalUiSuppression();
            _owner = null;
        }
    }
}
