using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;

namespace BabelPlayer.WinUI;

public interface IStageOverlayTimer
{
    event Action? Tick;
    bool IsEnabled { get; }
    void Start();
    void Stop();
}

public sealed class DispatcherStageOverlayTimer : IStageOverlayTimer
{
    private readonly DispatcherTimer _timer;

    public DispatcherStageOverlayTimer(TimeSpan interval)
    {
        _timer = new DispatcherTimer
        {
            Interval = interval
        };
        _timer.Tick += HandleTick;
    }

    public event Action? Tick;

    public bool IsEnabled => _timer.IsEnabled;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void HandleTick(object? sender, object e)
    {
        Tick?.Invoke();
    }
}

public sealed class StageCoordinator : IDisposable
{
    private readonly FrameworkElement _stageRelativeTo;
    private readonly IWindowModeService _windowModeService;
    private readonly IVideoPresenter _videoPresenter;
    private readonly ISubtitlePresenter _subtitlePresenter;
    private readonly Func<IFullscreenOverlayWindow>? _overlayFactory;
    private readonly IStageOverlayTimer _fullscreenOverlayTimer;
    private SubtitlePresentationModel _currentSubtitlePresentation = new();
    private SubtitleStyleSettings _currentSubtitleStyle = new();
    private int _modalSuppressionCount;
    private bool _hasLoadedMedia;
    private bool _isWindowActive = true;
    private bool _isFullscreenOverlayInteracting;
    private bool _isPositionScrubbing;
    private bool _fullscreenOverlayRequested;
    private long _fullscreenOverlayHideBlockedUntilTick;
    private long _lastOverlayTimerResetTick;
    private IFullscreenOverlayWindow? _fullscreenOverlayWindow;

    public StageCoordinator(
        FrameworkElement stageRelativeTo,
        IWindowModeService windowModeService,
        IVideoPresenter videoPresenter,
        ISubtitlePresenter subtitlePresenter,
        Func<IFullscreenOverlayWindow>? overlayFactory = null,
        IStageOverlayTimer? fullscreenOverlayTimer = null)
    {
        _stageRelativeTo = stageRelativeTo;
        _windowModeService = windowModeService;
        _videoPresenter = videoPresenter;
        _subtitlePresenter = subtitlePresenter;
        _overlayFactory = overlayFactory;
        _fullscreenOverlayTimer = fullscreenOverlayTimer ?? new DispatcherStageOverlayTimer(TimeSpan.FromSeconds(2.5));
        _fullscreenOverlayTimer.Tick += HandleFullscreenOverlayTimerTick;
    }

    public bool IsFullscreenOverlayVisible => _fullscreenOverlayWindow?.IsOverlayVisible == true;

    public IFullscreenOverlayWindow EnsureFullscreenOverlayWindow()
    {
        if (_fullscreenOverlayWindow is not null)
        {
            return _fullscreenOverlayWindow;
        }

        if (_overlayFactory is null)
        {
            throw new InvalidOperationException("No fullscreen overlay window factory was configured.");
        }

        _fullscreenOverlayWindow = _overlayFactory();
        _fullscreenOverlayWindow.ActivityDetected += HandleOverlayActivityDetected;
        _fullscreenOverlayWindow.InteractionStateChanged += HandleOverlayInteractionStateChanged;
        return _fullscreenOverlayWindow;
    }

    public void HandleWindowModeChanged(PlaybackWindowMode mode)
    {
        if (mode == PlaybackWindowMode.Fullscreen)
        {
            _fullscreenOverlayRequested = true;
            UpdateOverlayVisibility();
            return;
        }

        _fullscreenOverlayRequested = false;
        HideFullscreenOverlayWindow();
        RefreshSubtitlePresentation();
    }

    public void HandleWindowActivationChanged(bool isActive)
    {
        _isWindowActive = isActive;
        if (!isActive)
        {
            HideFullscreenOverlayWindow();
            _subtitlePresenter.Hide();
            return;
        }

        UpdateOverlayVisibility();
        RefreshSubtitlePresentation();
    }

    public void HandlePointerActivity()
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen)
        {
            return;
        }

        RegisterFullscreenOverlayInteraction();
        _fullscreenOverlayRequested = true;
        UpdateOverlayVisibility();
    }

    public void HandleScrubbingChanged(bool isScrubbing)
    {
        _isPositionScrubbing = isScrubbing;
        if (isScrubbing && _windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            _fullscreenOverlayRequested = true;
        }

        UpdateOverlayVisibility();
    }

    public void HandleStageLayoutChanged()
    {
        if (IsFullscreenOverlayVisible)
        {
            EnsureFullscreenOverlayWindow().PositionOverlay(_windowModeService.GetCurrentDisplayBounds());
        }

        RefreshSubtitlePresentation();
    }

    public void HandleAutoHideTick()
    {
        _fullscreenOverlayTimer.Stop();
        if (Environment.TickCount64 < _fullscreenOverlayHideBlockedUntilTick)
        {
            ScheduleFullscreenOverlayAutoHide();
            return;
        }

        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen && !_isPositionScrubbing && !_isFullscreenOverlayInteracting)
        {
            _fullscreenOverlayRequested = false;
            UpdateOverlayVisibility();
        }
    }

    public void RegisterFullscreenOverlayInteraction(int holdMilliseconds = 1800)
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen)
        {
            return;
        }

        _fullscreenOverlayHideBlockedUntilTick = Math.Max(_fullscreenOverlayHideBlockedUntilTick, Environment.TickCount64 + holdMilliseconds);
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
        _currentSubtitlePresentation = model;
        _currentSubtitleStyle = style;
        _hasLoadedMedia = hasLoadedMedia;
        RefreshSubtitlePresentation();
    }

    public IDisposable SuppressModalUi()
    {
        _modalSuppressionCount++;
        HideFullscreenOverlayWindow();
        _subtitlePresenter.Hide();
        return new ModalUiSuppressionScope(this);
    }

    public void Dispose()
    {
        _fullscreenOverlayTimer.Stop();
        _fullscreenOverlayTimer.Tick -= HandleFullscreenOverlayTimerTick;
        if (_fullscreenOverlayWindow is not null)
        {
            _fullscreenOverlayWindow.ActivityDetected -= HandleOverlayActivityDetected;
            _fullscreenOverlayWindow.InteractionStateChanged -= HandleOverlayInteractionStateChanged;
            _fullscreenOverlayWindow.CloseOverlay();
            _fullscreenOverlayWindow = null;
        }
    }

    private void UpdateOverlayVisibility()
    {
        if (_modalSuppressionCount > 0 || _windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen || !_isWindowActive)
        {
            HideFullscreenOverlayWindow();
            return;
        }

        if (!_fullscreenOverlayRequested)
        {
            HideFullscreenOverlayWindow();
            return;
        }

        ShowFullscreenOverlayWindow();
    }

    private void ShowFullscreenOverlayWindow()
    {
        var overlayWindow = EnsureFullscreenOverlayWindow();
        overlayWindow.PositionOverlay(_windowModeService.GetCurrentDisplayBounds());
        overlayWindow.ShowOverlay(_windowModeService.GetCurrentDisplayBounds());
        RefreshSubtitlePresentation();
        ScheduleFullscreenOverlayAutoHide();
    }

    private void HideFullscreenOverlayWindow()
    {
        _fullscreenOverlayTimer.Stop();
        _fullscreenOverlayWindow?.HideOverlay();
        RefreshSubtitlePresentation();
    }

    private void ScheduleFullscreenOverlayAutoHide()
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen || !IsFullscreenOverlayVisible || _isPositionScrubbing || _isFullscreenOverlayInteracting)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_fullscreenOverlayTimer.IsEnabled && now - _lastOverlayTimerResetTick < 140)
        {
            return;
        }

        _lastOverlayTimerResetTick = now;
        _fullscreenOverlayTimer.Stop();
        _fullscreenOverlayTimer.Start();
    }

    private void RefreshSubtitlePresentation()
    {
        if (_modalSuppressionCount > 0 || !_isWindowActive || !_hasLoadedMedia || !_currentSubtitlePresentation.IsVisible)
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

        _subtitlePresenter.ApplyStyle(_currentSubtitleStyle);
        _subtitlePresenter.Present(_currentSubtitlePresentation, stageBounds, GetSubtitleBottomOffset(_currentSubtitleStyle));
    }

    private int GetSubtitleBottomOffset(SubtitleStyleSettings style)
    {
        var styleOffset = (int)Math.Round(style.BottomMargin);
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            return (IsFullscreenOverlayVisible ? 248 : 68) + styleOffset;
        }

        return 44 + styleOffset;
    }

    private void HandleOverlayActivityDetected()
    {
        HandlePointerActivity();
    }

    private void HandleOverlayInteractionStateChanged(bool isInteracting)
    {
        _isFullscreenOverlayInteracting = isInteracting;
        RegisterFullscreenOverlayInteraction();
        if (isInteracting)
        {
            _fullscreenOverlayTimer.Stop();
            return;
        }

        UpdateOverlayVisibility();
    }

    private void HandleFullscreenOverlayTimerTick()
    {
        HandleAutoHideTick();
    }

    private void ReleaseModalUiSuppression()
    {
        if (_modalSuppressionCount == 0)
        {
            return;
        }

        _modalSuppressionCount--;
        if (_modalSuppressionCount > 0)
        {
            return;
        }

        UpdateOverlayVisibility();
        RefreshSubtitlePresentation();
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
