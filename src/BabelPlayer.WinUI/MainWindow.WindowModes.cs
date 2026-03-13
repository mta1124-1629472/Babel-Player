using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using BabelPlayer.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace BabelPlayer.WinUI;

public sealed partial class MainWindow
{
    private void BrowserPaneToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Browser.IsVisible = BrowserPaneToggle.IsChecked == true;
        SyncPaneLayout(RootGrid.ActualWidth);
        PersistLayoutPreferences();
    }

    private void PlaylistPaneToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Queue.IsVisible = PlaylistPaneToggle.IsChecked == true;
        SyncPaneLayout(RootGrid.ActualWidth);
        PersistLayoutPreferences();
    }

    private void CloseBrowserPane_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Browser.IsVisible = false;
        BrowserPaneToggle.IsChecked = false;
        SyncPaneLayout(RootGrid.ActualWidth);
        PersistLayoutPreferences();
    }

    private void ClosePlaylistPane_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Queue.IsVisible = false;
        PlaylistPaneToggle.IsChecked = false;
        SyncPaneLayout(RootGrid.ActualWidth);
        PersistLayoutPreferences();
    }

    private async void ImmersiveToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressWindowModeButtonChanges)
        {
            return;
        }

        var targetMode = _windowModeService.CurrentMode == PlaybackWindowMode.Borderless
            ? PlaybackWindowMode.Standard
            : PlaybackWindowMode.Borderless;

        await SetWindowModeAsync(targetMode);
    }

    private async void FullscreenToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressWindowModeButtonChanges)
        {
            return;
        }

        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            await ExitFullscreenAsync();
            return;
        }

        await EnterFullscreenAsync();
    }

    private async void PictureInPictureToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressWindowModeButtonChanges)
        {
            return;
        }

        var targetMode = _windowModeService.CurrentMode == PlaybackWindowMode.PictureInPicture
            ? PlaybackWindowMode.Standard
            : PlaybackWindowMode.PictureInPicture;

        await SetWindowModeAsync(targetMode);
    }

    private async void ExitFullscreenOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        await ExitFullscreenAsync();
    }

    private void ThemeToggleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item)
        {
            ApplyTheme(item.IsChecked);
        }
    }

    private void ThemeToggleButton_Checked(object sender, RoutedEventArgs e) => ApplyTheme(isDark: true);

    private void ThemeToggleButton_Unchecked(object sender, RoutedEventArgs e) => ApplyTheme(isDark: false);

    private void LanguageToolsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLanguageToolsEffectivelyExpanded())
        {
            _isLanguageToolsExpanded = false;
            _isLanguageToolsAutoCollapseOverridden = false;
        }
        else
        {
            _isLanguageToolsExpanded = true;
            _isLanguageToolsAutoCollapseOverridden = _autoCollapsedLanguageToolsForPortraitVideo;
        }

        UpdateLanguageToolsDrawerState();
        RefreshStageLayoutAfterLanguageToolsChange();
    }

    private void UpdateLanguageToolsDrawerState()
    {
        if (LanguageToolsToggleButton is null || LanguageToolsContentBorder is null)
        {
            return;
        }

        var isEffectivelyExpanded = IsLanguageToolsEffectivelyExpanded();
        LanguageToolsContentBorder.Visibility = isEffectivelyExpanded ? Visibility.Visible : Visibility.Collapsed;
        var header = new Grid
        {
            ColumnSpacing = 12
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Language Tools",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var actionText = new TextBlock
        {
            Text = _autoCollapsedLanguageToolsForPortraitVideo && !_isLanguageToolsAutoCollapseOverridden
                ? "Show"
                : isEffectivelyExpanded ? "Hide" : "Show",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 209, 219, 233))
        };
        Grid.SetColumn(actionText, 1);
        header.Children.Add(actionText);
        LanguageToolsToggleButton.Content = header;
        UpdateLanguageToolsResponsiveLayout();
    }

    private bool IsLanguageToolsEffectivelyExpanded()
    {
        return _isLanguageToolsExpanded
            && (!_autoCollapsedLanguageToolsForPortraitVideo || _isLanguageToolsAutoCollapseOverridden);
    }

    private void UpdatePortraitVideoLanguageToolsState()
    {
        var shouldAutoCollapse = false;
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Standard
            && LanguageToolsPane is not null
            && PlayerHost is not null)
        {
            var transport = _currentShellProjection.Transport;
            var metrics = GetTransportVideoMetrics(transport);
            var displayWidth = metrics.VideoDisplayWidth > 0
                ? metrics.VideoDisplayWidth
                : metrics.VideoWidth;
            var displayHeight = metrics.VideoDisplayHeight > 0
                ? metrics.VideoDisplayHeight
                : metrics.VideoHeight;

            if (displayWidth > 0
                && displayHeight > 0
                && displayHeight > displayWidth
                && VideoStageSurface.ActualWidth > 0
                && VideoStageSurface.ActualHeight > 0)
            {
                var requiredHeightAtCurrentWidth = VideoStageSurface.ActualWidth * displayHeight / (double)displayWidth;
                shouldAutoCollapse = requiredHeightAtCurrentWidth > VideoStageSurface.ActualHeight + 8;
            }
        }

        if (!shouldAutoCollapse)
        {
            _isLanguageToolsAutoCollapseOverridden = false;
        }

        if (_autoCollapsedLanguageToolsForPortraitVideo != shouldAutoCollapse)
        {
            _autoCollapsedLanguageToolsForPortraitVideo = shouldAutoCollapse;
            UpdateLanguageToolsDrawerState();
            RefreshStageLayoutAfterLanguageToolsChange();
        }
    }

    private void RefreshStageLayoutAfterLanguageToolsChange()
    {
        if (PlayerHost is null)
        {
            return;
        }

        PlayerHost.RequestHostBoundsSync();
        UpdateSubtitleVisibility();
        _stageCoordinator?.HandleStageLayoutChanged();
    }

    private void ApplyTheme(bool isDark)
    {
        ViewModel.IsDarkTheme = isDark;
        if (ThemeToggleMenuItem is not null)
        {
            ThemeToggleMenuItem.IsChecked = isDark;
        }

        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
    }

    private void TryApplySystemBackdrop()
    {
        if (_hasAttemptedSystemBackdrop)
        {
            return;
        }

        _hasAttemptedSystemBackdrop = true;
        if (!MicaController.IsSupported())
        {
            return;
        }

        try
        {
            _micaBackdrop = new MicaBackdrop();
            SystemBackdrop = _micaBackdrop;
        }
        catch
        {
            SystemBackdrop = null;
            _micaBackdrop = null;
        }
    }

    private void SyncPaneLayout(double width)
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Standard)
        {
            BrowserPane.Visibility = Visibility.Collapsed;
            PlaylistPane.Visibility = Visibility.Collapsed;
            BrowserColumn.Width = new GridLength(0);
            PlaylistColumn.Width = new GridLength(0);
            ShellContentGrid.ColumnSpacing = 0;
            BrowserPaneToggle.IsChecked = ViewModel.Browser.IsVisible;
            PlaylistPaneToggle.IsChecked = ViewModel.Queue.IsVisible;
            return;
        }

        var showBrowser = ViewModel.Browser.IsVisible && width >= GetBrowserDrawerVisibilityThreshold();
        var showPlaylistAsPane = ViewModel.Queue.IsVisible && width >= GetPlaylistDrawerVisibilityThreshold();
        var showPlaylistAsOverlay = ViewModel.Queue.IsVisible && !showPlaylistAsPane;
        var browserWidth = GetPreferredBrowserDrawerWidth(width);
        var playlistWidth = GetPreferredPlaylistDrawerWidth(width);
        if (showBrowser && showPlaylistAsPane && width < 1500)
        {
            browserWidth = 260;
            playlistWidth = 288;
        }

        BrowserPane.Visibility = showBrowser ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPane.Visibility = showPlaylistAsPane || showPlaylistAsOverlay ? Visibility.Visible : Visibility.Collapsed;
        BrowserColumn.Width = showBrowser ? new GridLength(browserWidth) : new GridLength(0);
        PlaylistColumn.Width = showPlaylistAsPane ? new GridLength(playlistWidth) : new GridLength(0);
        ShellContentGrid.ColumnSpacing = showBrowser && showPlaylistAsPane
            ? 16
            : showBrowser || showPlaylistAsPane
                ? 12
                : 0;
        if (showPlaylistAsOverlay)
        {
            Grid.SetColumn(PlaylistPane, 0);
            Grid.SetColumnSpan(PlaylistPane, 3);
            PlaylistPane.Width = GetOverlayPlaylistDrawerWidth(width);
            PlaylistPane.MaxWidth = PlaylistPane.Width;
            PlaylistPane.HorizontalAlignment = HorizontalAlignment.Right;
            PlaylistPane.VerticalAlignment = VerticalAlignment.Stretch;
            PlaylistPane.Margin = new Thickness(16, 0, 0, 0);
            Canvas.SetZIndex(PlaylistPane, 10);
        }
        else
        {
            Grid.SetColumn(PlaylistPane, 2);
            Grid.SetColumnSpan(PlaylistPane, 1);
            PlaylistPane.Width = double.NaN;
            PlaylistPane.MaxWidth = double.PositiveInfinity;
            PlaylistPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            PlaylistPane.VerticalAlignment = VerticalAlignment.Stretch;
            PlaylistPane.Margin = new Thickness(0);
            Canvas.SetZIndex(PlaylistPane, 0);
        }

        BrowserPaneToggle.IsChecked = ViewModel.Browser.IsVisible;
        PlaylistPaneToggle.IsChecked = ViewModel.Queue.IsVisible;
    }

    private void ApplyAdaptiveStandardLayout(double height)
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Standard || height <= 0)
        {
            return;
        }

        var narrowWindow = RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < 980;
        var compactLayout = height < 760 || narrowWindow;
        var hideLanguageTools = height < 640;
        var hideTransport = height < 580;
        var hideTimeline = height < 520;

        TimelinePane.Visibility = hideTimeline ? Visibility.Collapsed : Visibility.Visible;
        TransportPane.Visibility = hideTransport ? Visibility.Collapsed : Visibility.Visible;
        LanguageToolsPane.Visibility = hideLanguageTools ? Visibility.Collapsed : Visibility.Visible;
        CenterStageGrid.RowSpacing = compactLayout ? 8 : 10;
        TimelinePane.Padding = compactLayout ? new Thickness(10, 6, 10, 6) : new Thickness(12, 8, 12, 8);
        TransportPane.Padding = compactLayout ? new Thickness(8, 4, 8, 4) : new Thickness(10, 6, 10, 6);
        LanguageToolsPane.Padding = compactLayout ? new Thickness(10, 8, 10, 8) : new Thickness(12, 10, 12, 10);
        ShellContentGrid.Padding = compactLayout ? new Thickness(12, 6, 12, 6) : new Thickness(14, 8, 14, 8);
        PlayerPane.MinHeight = compactLayout ? 240 : 300;
        UpdatePortraitVideoLanguageToolsState();
    }

    private static double GetBrowserDrawerVisibilityThreshold() => 980;

    private static double GetPlaylistDrawerVisibilityThreshold() => 1320;

    private static double GetPreferredBrowserDrawerWidth(double width)
        => width >= 1560 ? 312 : 276;

    private static double GetPreferredPlaylistDrawerWidth(double width)
        => width >= 1560 ? 336 : 300;

    private static double GetOverlayPlaylistDrawerWidth(double width)
    {
        var preferredWidth = Math.Clamp(Math.Round(width * 0.38), 240, 360);
        var maxAvailableWidth = Math.Max(width - 32, 240);
        return Math.Min(preferredWidth, maxAvailableWidth);
    }

    private void TryApplyStandardAutoFit()
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Standard)
        {
            return;
        }

        var transport = _currentShellProjection.Transport;
        var sourcePath = transport.Path;
        if (string.IsNullOrWhiteSpace(sourcePath) || (_pendingAutoFitPath is not null && !string.Equals(_pendingAutoFitPath, sourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var metrics = GetTransportVideoMetrics(transport);
        var displayWidth = metrics.VideoDisplayWidth > 0 ? metrics.VideoDisplayWidth : metrics.VideoWidth;
        var displayHeight = metrics.VideoDisplayHeight > 0 ? metrics.VideoDisplayHeight : metrics.VideoHeight;
        if (displayWidth <= 0 || displayHeight <= 0 || VideoStageSurface.ActualWidth <= 0 || VideoStageSurface.ActualHeight <= 0)
        {
            return;
        }

        var signature = $"{sourcePath}|{displayWidth}x{displayHeight}|b:{ViewModel.Browser.IsVisible}|q:{ViewModel.Queue.IsVisible}|lt:{IsLanguageToolsEffectivelyExpanded()}";
        if (_pendingAutoFitPath is null
            && string.Equals(signature, _lastAutoFitSignature, StringComparison.Ordinal))
        {
            return;
        }

        var currentBounds = _windowModeService.CurrentBounds;
        var workArea = _windowModeService.GetCurrentDisplayBounds(workArea: true);
        var stageWidth = Math.Max(VideoStageSurface.ActualWidth, 1);
        var stageHeight = Math.Max(VideoStageSurface.ActualHeight, 1);
        var nonStageWidth = Math.Max(currentBounds.Width - stageWidth, 0);
        var nonStageHeight = Math.Max(currentBounds.Height - stageHeight, 0);
        var aspectRatio = displayWidth / (double)displayHeight;
        var maxStageWidth = Math.Max(workArea.Width - nonStageWidth, 320);
        var maxStageHeight = Math.Max(workArea.Height - nonStageHeight, 180);
        var desiredStageWidth = Math.Min(maxStageWidth, maxStageHeight * aspectRatio);
        var desiredStageHeight = desiredStageWidth / aspectRatio;
        if (desiredStageHeight > maxStageHeight)
        {
            desiredStageHeight = maxStageHeight;
            desiredStageWidth = desiredStageHeight * aspectRatio;
        }

        var desiredWindowWidth = (int)Math.Round(nonStageWidth + desiredStageWidth);
        var desiredWindowHeight = (int)Math.Round(nonStageHeight + desiredStageHeight);
        var minimumWindowWidth = (int)Math.Ceiling(nonStageWidth + 420);
        var drawerPreservingWidth = 0d;
        if (ViewModel.Browser.IsVisible)
        {
            drawerPreservingWidth = Math.Max(
                drawerPreservingWidth,
                GetBrowserDrawerVisibilityThreshold());
        }

        if (ViewModel.Queue.IsVisible)
        {
            var currentWindowWidth = _windowModeService.CurrentBounds.Width;
            if (currentWindowWidth >= GetPlaylistDrawerVisibilityThreshold())
            {
                drawerPreservingWidth = Math.Max(
                    drawerPreservingWidth,
                    GetPlaylistDrawerVisibilityThreshold());
            }
        }

        minimumWindowWidth = Math.Max(minimumWindowWidth, (int)Math.Ceiling(drawerPreservingWidth));
        if (ViewModel.Browser.IsVisible || ViewModel.Queue.IsVisible)
        {
            // Keep open drawers stable when auto-fitting to newly loaded media.
            minimumWindowWidth = Math.Max(minimumWindowWidth, currentBounds.Width);
        }

        desiredWindowWidth = Math.Clamp(desiredWindowWidth, minimumWindowWidth, workArea.Width);
        var minimumWindowHeight = Math.Max((int)Math.Ceiling(nonStageHeight + 200), 700);
        desiredWindowHeight = Math.Clamp(desiredWindowHeight, minimumWindowHeight, workArea.Height);
        var x = workArea.X + Math.Max((workArea.Width - desiredWindowWidth) / 2, 0);
        var y = workArea.Y + Math.Max((workArea.Height - desiredWindowHeight) / 2, 0);
        var desiredBounds = new RectInt32(x, y, desiredWindowWidth, desiredWindowHeight);
        var isWidthSettled = Math.Abs(currentBounds.Width - desiredWindowWidth) <= 4;
        var isHeightSettled = Math.Abs(currentBounds.Height - desiredWindowHeight) <= 4;
        if (isWidthSettled && isHeightSettled)
        {
            _lastAutoFitSignature = signature;
            _pendingAutoFitPath = null;
            PlayerHost.RequestHostBoundsSync();
            return;
        }

        _windowModeService.ApplyStandardBounds(desiredBounds);
        PlayerHost.RequestHostBoundsSync();
    }

    private async Task SetWindowModeAsync(PlaybackWindowMode mode)
    {
        await _windowModeService.SetModeAsync(mode);
        _diagnosticsContext.UpdateWindowMode(mode.ToString());
        _logger.LogInfo("Window mode changed.", BabelLogContext.Create(("mode", mode)));
        ApplyWindowModeChrome(mode);
        PlayerHost.RequestHostBoundsSync();
        SyncWindowModeButtons(mode);
        UpdateOverlayControlState();
        if (!_isInitializingShellState)
        {
            PersistLayoutPreferences();
        }
        ShowStatus(mode switch
        {
            PlaybackWindowMode.Borderless => "Immersive mode enabled.",
            PlaybackWindowMode.Fullscreen => "Fullscreen enabled.",
            PlaybackWindowMode.PictureInPicture => "Picture in picture enabled.",
            _ => "Standard window mode restored."
        });
    }

    private async Task EnterFullscreenAsync()
    {
        await SetWindowModeAsync(PlaybackWindowMode.Fullscreen);
    }

    private async Task ExitFullscreenAsync()
    {
        await SetWindowModeAsync(PlaybackWindowMode.Standard);
    }

    private void ApplyWindowModeChrome(PlaybackWindowMode mode)
    {
        var isPlayerOnly = mode is PlaybackWindowMode.Fullscreen or PlaybackWindowMode.PictureInPicture;
        var isNonStandard = mode != PlaybackWindowMode.Standard;
        UnifiedHeaderBar.Visibility = isNonStandard ? Visibility.Collapsed : Visibility.Visible;
        AppTitleBar.Visibility = isNonStandard ? Visibility.Collapsed : Visibility.Visible;
        ShellCommandBar.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        HideStatusOverlay();
        TimelinePane.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        TransportPane.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        LanguageToolsPane.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        ShellContentGrid.Padding = isNonStandard ? new Thickness(0) : new Thickness(14, 8, 14, 8);
        ShellContentGrid.ColumnSpacing = isNonStandard ? 0 : ShellContentGrid.ColumnSpacing;
        PlayerPane.Padding = isNonStandard ? new Thickness(0) : new Thickness(12);
        PlayerPane.BorderThickness = isNonStandard ? new Thickness(0) : new Thickness(1);
        PlayerPane.CornerRadius = isNonStandard ? new CornerRadius(0) : new CornerRadius(24);
        DecoderBadge.Visibility = mode == PlaybackWindowMode.Fullscreen ? Visibility.Collapsed : Visibility.Visible;
        if (StatusOverlayBorder is not null)
        {
            StatusOverlayBorder.Visibility = mode == PlaybackWindowMode.Fullscreen ? Visibility.Collapsed : StatusOverlayBorder.Visibility;
        }
        SubtitleOverlayBorder.Margin = mode == PlaybackWindowMode.Fullscreen
            ? new Thickness(32, 0, 32, 110)
            : new Thickness(24);
        if (mode == PlaybackWindowMode.Fullscreen)
        {
            EnsureStageOverlayControls();
        }
        _stageCoordinator.HandleWindowModeChanged(mode);

        SyncPaneLayout(RootGrid.ActualWidth);
        if (mode == PlaybackWindowMode.Standard)
        {
            ApplyAdaptiveStandardLayout(RootGrid.ActualHeight);
            TryApplyStandardAutoFit();
        }

        UpdatePortraitVideoLanguageToolsState();
        UpdateSubtitleVisibility();
    }

    private void SyncWindowModeButtons(PlaybackWindowMode mode)
    {
        _suppressWindowModeButtonChanges = true;
        ImmersiveToggleButton.IsChecked = mode == PlaybackWindowMode.Borderless;
        FullscreenToggleButton.IsChecked = mode == PlaybackWindowMode.Fullscreen;
        PictureInPictureToggleButton.IsChecked = mode == PlaybackWindowMode.PictureInPicture;
        _suppressWindowModeButtonChanges = false;
    }

    private void EnsureStageOverlayControls()
    {
        if (OverlayPlayPauseButton is not null
            && OverlaySubtitleToggleButton is not null
            && OverlaySubtitleModeButton is not null
            && OverlaySubtitleStyleButton is not null
            && OverlayPipButton is not null
            && OverlayImmersiveButton is not null
            && OverlaySettingsButton is not null
            && OverlayExitFullscreenButton is not null
            && FullscreenPositionSlider is not null
            && FullscreenCurrentTimeTextBlock is not null
            && FullscreenDurationTextBlock is not null)
        {
            return;
        }

        var overlayWindow = _stageCoordinator.EnsureFullscreenOverlayWindow();
        OverlayPlayPauseButton = overlayWindow.PlayPauseButton;
        OverlayPlayPauseButton.Click += PlayPauseButton_Click;
        OverlaySubtitleToggleButton = overlayWindow.SubtitleToggleButton;
        OverlaySubtitleToggleButton.Click += OverlaySubtitleToggleButton_Click;
        OverlaySubtitleModeButton = overlayWindow.SubtitleModeButton;
        OverlaySubtitleStyleButton = overlayWindow.SubtitleStyleButton;
        OverlayPipButton = overlayWindow.PipButton;
        OverlayPipButton.Click += PictureInPictureToggleButton_Click;
        OverlayImmersiveButton = overlayWindow.ImmersiveButton;
        OverlayImmersiveButton.Click += ImmersiveToggleButton_Click;
        OverlaySettingsButton = overlayWindow.SettingsButton;
        OverlayExitFullscreenButton = overlayWindow.ExitFullscreenButton;
        OverlayExitFullscreenButton.Click += ExitFullscreenOverlayButton_Click;
        FullscreenPositionSlider = overlayWindow.PositionSlider;
        FullscreenPositionSlider.ValueChanged += FullscreenPositionSlider_ValueChanged;
        AttachScrubberHandlers(FullscreenPositionSlider);
        FullscreenCurrentTimeTextBlock = overlayWindow.CurrentTimeTextBlock;
        FullscreenDurationTextBlock = overlayWindow.DurationTextBlock;
        RefreshOverlayFlyouts();
    }

    private void UpdateOverlayControlState()
    {
        if (SubtitleVisibilityToggleButton is not null)
        {
            SubtitleVisibilityToggleButton.IsChecked = ViewModel.Settings.SubtitleRenderMode != SubtitleRenderMode.Off;
            SubtitleVisibilityToggleButton.Label = ViewModel.Settings.SubtitleRenderMode == SubtitleRenderMode.Off
                ? "Subtitles Off"
                : "Subtitles On";
        }

        if (LanguageToolsSubtitleToggleButton is not null)
        {
            LanguageToolsSubtitleToggleButton.IsChecked = ViewModel.Settings.SubtitleRenderMode != SubtitleRenderMode.Off;
            LanguageToolsSubtitleToggleButton.Content = ViewModel.Settings.SubtitleRenderMode == SubtitleRenderMode.Off
                ? "Subtitles Off"
                : "Subtitles On";
        }

        if (LanguageToolsSubtitleStatusTextBlock is not null)
        {
            LanguageToolsSubtitleStatusTextBlock.Text = ViewModel.Settings.SubtitleRenderMode == SubtitleRenderMode.Off
                ? "Disabled"
                : "Enabled";
        }

        if (OverlayPlayPauseButton is not null)
        {
            OverlayPlayPauseButton.Content = new SymbolIcon(ViewModel.Transport.IsPaused ? Symbol.Play : Symbol.Pause);
            SetControlHint(OverlayPlayPauseButton, ViewModel.Transport.IsPaused ? "Play" : "Pause");
        }

        if (OverlaySubtitleToggleButton is not null)
        {
            OverlaySubtitleToggleButton.Content = ViewModel.Settings.SubtitleRenderMode == SubtitleRenderMode.Off
                ? "Subtitles Off"
                : "Subtitles On";
        }

        if (OverlaySubtitleModeButton is not null)
        {
            OverlaySubtitleModeButton.Content = $"Mode: {FormatSubtitleRenderModeButtonLabel(GetEffectiveSubtitleRenderMode())}";
        }

        if (OverlaySubtitleStyleButton is not null)
        {
            OverlaySubtitleStyleButton.Content = "Style";
        }

        RefreshOverlayFlyouts();
    }

    private void RefreshOverlayFlyouts()
    {
        if (OverlaySettingsButton is not null)
        {
            OverlaySettingsButton.Flyout = BuildPlaybackOptionsFlyout(capturePrimaryReferences: false);
        }

        if (OverlaySubtitleModeButton is not null)
        {
            OverlaySubtitleModeButton.Flyout = CreateSubtitleModeFlyout();
        }

        if (OverlaySubtitleStyleButton is not null)
        {
            OverlaySubtitleStyleButton.Flyout = CreateSubtitleStyleFlyout();
        }
    }

    private void PositionFullscreenOverlay()
    {
        _stageCoordinator.HandleStageLayoutChanged();
    }

    private async void FireAndForget(Task task, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unhandled error in fire-and-forget call from {caller}.", ex);
        }
    }

    private async void FireAndForget(ValueTask task, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unhandled error in fire-and-forget call from {caller}.", ex);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool ClientToScreen(nint hWnd, ref NativePoint lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
    }

    private void PlayerPane_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ShowFullscreenOverlay();
    }

    private void RegisterFullscreenOverlayInteraction(int holdMilliseconds = 1800)
    {
        _stageCoordinator.RegisterFullscreenOverlayInteraction(holdMilliseconds);
    }

    private void ShowFullscreenOverlay()
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen)
        {
            return;
        }

        EnsureStageOverlayControls();
        _stageCoordinator.HandlePointerActivity();
        UpdateSubtitleVisibility();
    }

    private IDisposable SuppressModalUi()
    {
        return new CombinedSuppressionScope(_stageCoordinator.SuppressModalUi(), null);
    }

    private IDisposable SuppressDialogPresentation()
    {
        var hostSuppression = PlayerHost?.SuppressNativeHost();
        var modalSuppression = SuppressModalUi();
        return new CombinedSuppressionScope(hostSuppression, modalSuppression);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && IsAppStillForeground())
        {
            return;
        }

        var isWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        _stageCoordinator.HandleWindowActivationChanged(isWindowActive);
        if (!isWindowActive)
        {
            return;
        }

        TryApplySystemBackdrop();
        EnsureShellDropTargets();
        UpdateSubtitleVisibility();
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen && _stageCoordinator.IsFullscreenOverlayVisible)
        {
            PositionFullscreenOverlay();
        }
    }

    private void EnsureShellDropTargets()
    {
        if (_shellDropTargetsInitialized || RootGrid.XamlRoot is null)
        {
            return;
        }

        _shellDropTargetsInitialized = true;
        foreach (var target in GetShellDropTargets())
        {
            target.AllowDrop = true;
            target.DragOver += RootGrid_DragOver;
            target.Drop += RootGrid_Drop;
        }
    }

    private IEnumerable<UIElement> GetShellDropTargets()
    {
        yield return RootGrid;

        var optionalTargets = new UIElement?[]
        {
            UnifiedHeaderBar,
            AppTitleBar,
            ShellCommandBar,
            StatusOverlayBorder,
            ShellContentGrid,
            BrowserPane,
            PlayerPane,
            PlaylistPane,
            PlaylistList,
            TransportPane
        };

        foreach (var target in optionalTargets)
        {
            if (target is not null)
            {
                yield return target;
            }
        }
    }

    private void StatusOverlayTimer_Tick(object? sender, object e)
    {
        _statusOverlayTimer.Stop();
        HideStatusOverlay();
    }

    private void HideStatusOverlay()
    {
        if (StatusOverlayBorder is not null)
        {
            StatusOverlayBorder.Visibility = Visibility.Collapsed;
        }

        ViewModel.IsStatusOpen = false;
    }

    private void ShowStatus(string message, bool isError = false)
    {
        ViewModel.StatusMessage = message;
        ViewModel.IsStatusOpen = true;
        if (StatusOverlayBorder is not null && StatusOverlayTextBlock is not null)
        {
            StatusOverlayTextBlock.Text = message;
            StatusOverlayBorder.Background = new SolidColorBrush(
                isError
                    ? ColorHelper.FromArgb(220, 131, 32, 40)
                    : ColorHelper.FromArgb(196, 23, 29, 38));
            StatusOverlayBorder.Visibility = Visibility.Visible;
            _statusOverlayTimer.Stop();
            _statusOverlayTimer.Interval = TimeSpan.FromSeconds(isError ? 10 : 4);
            _statusOverlayTimer.Start();
        }

        StatusInfoBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = false;
        ViewModel.StatusFeed.Insert(0, message);
        if (ViewModel.StatusFeed.Count > 20)
        {
            ViewModel.StatusFeed.RemoveAt(ViewModel.StatusFeed.Count - 1);
        }

        if (isError)
        {
            _statusLogger.LogError("Status message displayed.", null, BabelLogContext.Create(("message", message)));
            return;
        }

        _statusLogger.LogInfo("Status message displayed.", BabelLogContext.Create(("message", message)));
    }

    private void UpdatePlaybackDiagnostics(PlaybackStateSnapshot snapshot)
    {
        _diagnosticsContext.UpdatePlayback(new PlaybackDiagnosticsSummary
        {
            CurrentMediaPath = snapshot.Path,
            CurrentMediaDisplayName = string.IsNullOrWhiteSpace(snapshot.Path) ? null : Path.GetFileName(snapshot.Path),
            IsPaused = snapshot.IsPaused,
            Position = snapshot.Position,
            Duration = snapshot.Duration,
            Volume = snapshot.Volume,
            IsMuted = snapshot.IsMuted,
            ActiveHardwareDecoder = snapshot.ActiveHardwareDecoder,
            VideoWidth = snapshot.VideoWidth,
            VideoHeight = snapshot.VideoHeight,
            VideoDisplayWidth = snapshot.VideoDisplayWidth,
            VideoDisplayHeight = snapshot.VideoDisplayHeight
        });
    }

    private void UpdatePlaybackDiagnostics(ShellTransportProjection transport)
    {
        var metrics = GetTransportVideoMetrics(transport);
        _diagnosticsContext.UpdatePlayback(new PlaybackDiagnosticsSummary
        {
            CurrentMediaPath = transport.Path,
            CurrentMediaDisplayName = string.IsNullOrWhiteSpace(transport.Path) ? null : Path.GetFileName(transport.Path),
            IsPaused = transport.IsPaused,
            Position = TimeSpan.FromSeconds(transport.PositionSeconds),
            Duration = TimeSpan.FromSeconds(transport.DurationSeconds),
            Volume = transport.Volume,
            IsMuted = transport.IsMuted,
            ActiveHardwareDecoder = transport.ActiveHardwareDecoder,
            VideoWidth = metrics.VideoWidth,
            VideoHeight = metrics.VideoHeight,
            VideoDisplayWidth = metrics.VideoDisplayWidth,
            VideoDisplayHeight = metrics.VideoDisplayHeight
        });
    }

    private static TransportVideoMetrics GetTransportVideoMetrics(ShellTransportProjection transport)
    {
        return new TransportVideoMetrics(
            GetTransportVideoMetric(transport, nameof(TransportVideoMetrics.VideoWidth)),
            GetTransportVideoMetric(transport, nameof(TransportVideoMetrics.VideoHeight)),
            GetTransportVideoMetric(transport, nameof(TransportVideoMetrics.VideoDisplayWidth)),
            GetTransportVideoMetric(transport, nameof(TransportVideoMetrics.VideoDisplayHeight)));
    }

    private static int GetTransportVideoMetric(ShellTransportProjection transport, string propertyName)
    {
        return transport.GetType().GetProperty(propertyName)?.GetValue(transport) as int? ?? 0;
    }

    private readonly record struct TransportVideoMetrics(
        int VideoWidth,
        int VideoHeight,
        int VideoDisplayWidth,
        int VideoDisplayHeight);

    private void UpdateQueueDiagnostics(PlaybackQueueSnapshot snapshot)
    {
        _diagnosticsContext.UpdateQueue(new QueueDiagnosticsSummary
        {
            NowPlayingDisplayName = snapshot.NowPlayingItem?.DisplayName,
            NowPlayingPath = snapshot.NowPlayingItem?.Path,
            UpNextCount = snapshot.QueueItems.Count,
            HistoryCount = snapshot.HistoryItems.Count
        });
    }

    private void UpdateWorkflowDiagnostics(SubtitleWorkflowSnapshot snapshot)
    {
        _diagnosticsContext.UpdateSubtitleWorkflow(new SubtitleWorkflowDiagnosticsSummary
        {
            SubtitleSource = snapshot.SubtitleSource.ToString(),
            IsCaptionGenerationInProgress = snapshot.IsCaptionGenerationInProgress,
            SelectedTranscriptionModelKey = snapshot.SelectedTranscriptionModelKey ?? string.Empty,
            SelectedTranslationModelKey = snapshot.SelectedTranslationModelKey ?? string.Empty,
            IsTranslationEnabled = snapshot.IsTranslationEnabled,
            SourceLanguage = snapshot.CurrentSourceLanguage ?? string.Empty,
            OverlayStatus = snapshot.OverlayStatus ?? string.Empty
        });
    }

    private sealed class CombinedSuppressionScope : IDisposable
    {
        private IDisposable? _first;
        private IDisposable? _second;

        public CombinedSuppressionScope(IDisposable? first, IDisposable? second)
        {
            _first = first;
            _second = second;
        }

        public void Dispose()
        {
            _second?.Dispose();
            _second = null;
            _first?.Dispose();
            _first = null;
        }
    }
}
