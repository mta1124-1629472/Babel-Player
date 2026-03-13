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
    private void BuildShell()
    {
        RootGrid ??= new Grid();
        Content = RootGrid;

        RootGrid.RowDefinitions.Clear();
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var shellBrush = new SolidColorBrush(ColorHelper.FromArgb(188, 17, 20, 27));
        var drawerBrush = new SolidColorBrush(ColorHelper.FromArgb(136, 23, 29, 38));
        var railBrush = new SolidColorBrush(ColorHelper.FromArgb(110, 20, 25, 34));
        var playerSurfaceBrush = new SolidColorBrush(ColorHelper.FromArgb(72, 12, 16, 22));
        var subtleBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(72, 120, 136, 160));
        var stageBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(126, 106, 126, 156));
        var accentBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 47, 111, 178));

        UnifiedHeaderBar = new Grid
        {
            Background = shellBrush,
            ColumnSpacing = 12,
            Padding = new Thickness(14, 8, 14, 8)
        };
        UnifiedHeaderBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        UnifiedHeaderBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(UnifiedHeaderBar, 0);
        RootGrid.Children.Add(UnifiedHeaderBar);

        AppTitleBar = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(6, 2, 12, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(AppTitleBar, 0);

        var titleBarGrid = new Grid
        {
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBarGrid.Children.Add(new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Source = new BitmapImage(new Uri("ms-appx:///BabelPlayer.ico"))
        });

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleStack, 1);
        WindowTitleTextBlock = new TextBlock
        {
            Text = "Babel Player",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        titleStack.Children.Add(WindowTitleTextBlock);
        WindowSubtitleTextBlock = new TextBlock
        {
            MaxWidth = 420,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219)),
            Visibility = Visibility.Collapsed
        };
        titleStack.Children.Add(WindowSubtitleTextBlock);
        titleBarGrid.Children.Add(titleStack);
        AppTitleBar.Child = titleBarGrid;
        UnifiedHeaderBar.Children.Add(AppTitleBar);

        ShellCommandBar = new CommandBar
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            IsDynamicOverflowEnabled = true
        };
        Grid.SetColumn(ShellCommandBar, 1);
        ShellCommandBar.PrimaryCommands.Add(CreatePrimaryCommand("Open", Symbol.OpenFile, OpenFile_Click));
        BrowserPaneToggle = new AppBarToggleButton
        {
            Label = "Library",
            Icon = new SymbolIcon(Symbol.Library)
        };
        BrowserPaneToggle.Click += BrowserPaneToggle_Click;
        SetControlHint(BrowserPaneToggle, "Toggle library");
        ShellCommandBar.PrimaryCommands.Add(BrowserPaneToggle);

        PlaylistPaneToggle = new AppBarToggleButton
        {
            Label = "Queue",
            Icon = new SymbolIcon(Symbol.List)
        };
        PlaylistPaneToggle.Click += PlaylistPaneToggle_Click;
        SetControlHint(PlaylistPaneToggle, "Toggle queue");
        ShellCommandBar.PrimaryCommands.Add(PlaylistPaneToggle);

        FullscreenToggleButton = new AppBarToggleButton
        {
            Label = "Fullscreen",
            Icon = new SymbolIcon(Symbol.FullScreen)
        };
        FullscreenToggleButton.Click += FullscreenToggleButton_Click;
        SetControlHint(FullscreenToggleButton, "Fullscreen");
        ShellCommandBar.PrimaryCommands.Add(FullscreenToggleButton);
        PictureInPictureToggleButton = new AppBarToggleButton
        {
            Label = "PiP",
            Icon = new SymbolIcon(Symbol.SwitchApps)
        };
        PictureInPictureToggleButton.Click += PictureInPictureToggleButton_Click;
        SetControlHint(PictureInPictureToggleButton, "Picture in picture");
        ShellCommandBar.PrimaryCommands.Add(PictureInPictureToggleButton);
        ImmersiveToggleButton = new AppBarToggleButton
        {
            Label = "Immersive",
            Icon = new SymbolIcon(Symbol.HideBcc)
        };
        ImmersiveToggleButton.Click += ImmersiveToggleButton_Click;
        SetControlHint(ImmersiveToggleButton, "Immersive mode");
        ShellCommandBar.PrimaryCommands.Add(ImmersiveToggleButton);

        PlaybackOptionsFlyout = BuildPlaybackOptionsFlyout(capturePrimaryReferences: true);
        PlaybackOptionsButton = new AppBarButton
        {
            Label = "Settings",
            Icon = new SymbolIcon(Symbol.Setting),
            Flyout = PlaybackOptionsFlyout
        };
        SetControlHint(PlaybackOptionsButton, "Settings");
        ShellCommandBar.PrimaryCommands.Add(PlaybackOptionsButton);
        UnifiedHeaderBar.Children.Add(ShellCommandBar);

        StatusInfoBar = new InfoBar
        {
            Visibility = Visibility.Collapsed,
            IsClosable = true,
            IsOpen = false,
            Message = string.Empty,
            Severity = InfoBarSeverity.Informational
        };

        ShellContentGrid = new Grid
        {
            Padding = new Thickness(14, 8, 14, 8),
            ColumnSpacing = 10
        };
        Grid.SetRow(ShellContentGrid, 1);
        BrowserColumn = new ColumnDefinition { Width = new GridLength(0) };
        PlaylistColumn = new ColumnDefinition { Width = new GridLength(0) };
        ShellContentGrid.ColumnDefinitions.Add(BrowserColumn);
        ShellContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ShellContentGrid.ColumnDefinitions.Add(PlaylistColumn);
        RootGrid.Children.Add(ShellContentGrid);

        BrowserPane = CreatePanelBorder(drawerBrush, subtleBorderBrush, 1, new CornerRadius(24), new Thickness(14, 12, 14, 14));
        BrowserPane.Visibility = Visibility.Collapsed;
        Grid.SetColumn(BrowserPane, 0);
        ShellContentGrid.Children.Add(BrowserPane);
        BrowserPane.Child = BuildBrowserPane();

        PlaybackLayoutRoot = new Grid
        {
            RowSpacing = 10
        };
        PlaybackLayoutRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        PlaybackLayoutRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        PlaybackLayoutRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        CenterStageGrid = PlaybackLayoutRoot;
        Grid.SetColumn(PlaybackLayoutRoot, 1);
        ShellContentGrid.Children.Add(PlaybackLayoutRoot);

        PlayerPane = CreatePanelBorder(playerSurfaceBrush, stageBorderBrush, 1, new CornerRadius(26), new Thickness(12));
        PlayerPane.MinHeight = 320;
        Grid.SetRow(PlayerPane, 0);
        PlaybackLayoutRoot.Children.Add(PlayerPane);
        PlayerPane.Child = BuildPlayerPane(accentBrush);

        TimelinePane = CreatePanelBorder(railBrush, subtleBorderBrush, 0, new CornerRadius(18), new Thickness(14, 10, 14, 10));
        Grid.SetRow(TimelinePane, 1);
        TimelinePane.Child = BuildTimelinePane();
        PlaybackLayoutRoot.Children.Add(TimelinePane);

        TransportPane = CreatePanelBorder(railBrush, subtleBorderBrush, 0, new CornerRadius(22), new Thickness(10, 6, 10, 6));
        TransportPane.Margin = new Thickness(0);
        Grid.SetRow(TransportPane, 2);
        TransportPane.Child = BuildTransportPane();
        PlaybackLayoutRoot.Children.Add(TransportPane);

        LanguageToolsPane = CreatePanelBorder(drawerBrush, subtleBorderBrush, 0, new CornerRadius(20), new Thickness(12, 10, 12, 10));
        Grid.SetRow(LanguageToolsPane, 2);
        LanguageToolsPane.Child = BuildLanguageToolsDrawer();
        RootGrid.Children.Add(LanguageToolsPane);

        PlaylistPane = CreatePanelBorder(drawerBrush, subtleBorderBrush, 1, new CornerRadius(24), new Thickness(14, 12, 14, 14));
        PlaylistPane.Visibility = Visibility.Collapsed;
        Grid.SetColumn(PlaylistPane, 2);
        Canvas.SetZIndex(PlaylistPane, 0);
        ShellContentGrid.Children.Add(PlaylistPane);
        PlaylistPane.Child = BuildPlaylistPane();
        RootGrid.SizeChanged += RootGrid_SizeChanged;
    }

    private Border CreatePanelBorder(
        Brush background,
        Brush borderBrush,
        double borderThickness = 1,
        CornerRadius? cornerRadius = null,
        Thickness? padding = null)
    {
        return new Border
        {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(borderThickness),
            CornerRadius = cornerRadius ?? new CornerRadius(24),
            Padding = padding ?? new Thickness(14)
        };
    }

    private AppBarButton CreatePrimaryCommand(string label, Symbol icon, RoutedEventHandler handler)
    {
        var button = new AppBarButton
        {
            Label = label,
            Icon = new SymbolIcon(icon)
        };
        button.Click += handler;
        SetControlHint(button, label);
        return button;
    }

    private static Button CreateDrawerActionButton(string glyph, string hint, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Padding = new Thickness(10, 8, 10, 8),
            Content = new FontIcon { Glyph = glyph }
        };
        button.Click += handler;
        SetControlHint(button, hint);
        return button;
    }

    private MenuFlyout BuildPlaybackOptionsFlyout(bool capturePrimaryReferences)
    {
        var flyout = new MenuFlyout();

        var themeToggleMenuItem = new ToggleMenuFlyoutItem
        {
            Text = "Dark Theme"
        };
        themeToggleMenuItem.Click += ThemeToggleMenuItem_Click;
        flyout.Items.Add(themeToggleMenuItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var playbackRateFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Playback Rate"
        };
        playbackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("0.75x", 0.75));
        playbackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("1.00x", 1.00));
        playbackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("1.25x", 1.25));
        playbackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("1.50x", 1.50));
        playbackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("2.00x", 2.00));
        flyout.Items.Add(playbackRateFlyoutSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var hardwareDecodingFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Hardware Decode"
        };
        hardwareDecodingFlyoutSubItem.Items.Add(CreateHardwareDecodingFlyoutItem("Auto Safe", HardwareDecodingMode.AutoSafe));
        hardwareDecodingFlyoutSubItem.Items.Add(CreateHardwareDecodingFlyoutItem("D3D11", HardwareDecodingMode.D3D11));
        hardwareDecodingFlyoutSubItem.Items.Add(CreateHardwareDecodingFlyoutItem("NVDEC", HardwareDecodingMode.Nvdec));
        hardwareDecodingFlyoutSubItem.Items.Add(CreateHardwareDecodingFlyoutItem("Software", HardwareDecodingMode.Software));
        flyout.Items.Add(hardwareDecodingFlyoutSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var aspectRatioFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Aspect Ratio"
        };
        aspectRatioFlyoutSubItem.Items.Add(CreateAspectRatioFlyoutItem("Auto", "auto"));
        aspectRatioFlyoutSubItem.Items.Add(CreateAspectRatioFlyoutItem("16:9", "16:9"));
        aspectRatioFlyoutSubItem.Items.Add(CreateAspectRatioFlyoutItem("4:3", "4:3"));
        aspectRatioFlyoutSubItem.Items.Add(CreateAspectRatioFlyoutItem("Fill", "-1"));
        flyout.Items.Add(aspectRatioFlyoutSubItem);

        var audioTracksFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Audio Track"
        };
        flyout.Items.Add(audioTracksFlyoutSubItem);

        var embeddedSubtitleTracksFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Embedded Subtitles"
        };
        flyout.Items.Add(embeddedSubtitleTracksFlyoutSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var subtitleDelayFlyoutSubItem = new MenuFlyoutSubItem();
        subtitleDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Back 50 ms", SubtitleDelayBack_Click));
        subtitleDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Forward 50 ms", SubtitleDelayForward_Click));
        subtitleDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Reset", ResetSubtitleDelay_Click));
        flyout.Items.Add(subtitleDelayFlyoutSubItem);

        var audioDelayFlyoutSubItem = new MenuFlyoutSubItem();
        audioDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Back 50 ms", AudioDelayBack_Click));
        audioDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Forward 50 ms", AudioDelayForward_Click));
        audioDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Reset", ResetAudioDelay_Click));
        flyout.Items.Add(audioDelayFlyoutSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var resumePlaybackToggleItem = new ToggleMenuFlyoutItem
        {
            Text = "Resume Playback"
        };
        resumePlaybackToggleItem.Click += ResumePlaybackToggleItem_Click;
        flyout.Items.Add(resumePlaybackToggleItem);

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Keyboard Shortcuts..."
        });
        ((MenuFlyoutItem)flyout.Items[^1]).Click += EditShortcuts_Click;

        var exportCurrentSubtitlesFlyoutItem = new MenuFlyoutItem
        {
            Text = "Export Current Subtitles",
            IsEnabled = false
        };
        exportCurrentSubtitlesFlyoutItem.Click += ExportCurrentSubtitles_Click;
        flyout.Items.Add(exportCurrentSubtitlesFlyoutItem);

        if (capturePrimaryReferences)
        {
            ThemeToggleMenuItem = themeToggleMenuItem;
            PlaybackRateFlyoutSubItem = playbackRateFlyoutSubItem;
            HardwareDecodingFlyoutSubItem = hardwareDecodingFlyoutSubItem;
            AspectRatioFlyoutSubItem = aspectRatioFlyoutSubItem;
            AudioTracksFlyoutSubItem = audioTracksFlyoutSubItem;
            EmbeddedSubtitleTracksFlyoutSubItem = embeddedSubtitleTracksFlyoutSubItem;
            SubtitleDelayFlyoutSubItem = subtitleDelayFlyoutSubItem;
            AudioDelayFlyoutSubItem = audioDelayFlyoutSubItem;
            ResumePlaybackToggleItem = resumePlaybackToggleItem;
            ExportCurrentSubtitlesFlyoutItem = exportCurrentSubtitlesFlyoutItem;
        }

        if (capturePrimaryReferences)
        {
            RebuildAudioTrackFlyout();
            RebuildEmbeddedSubtitleTrackFlyout();
            UpdatePlaybackRateFlyoutChecks();
            UpdateHardwareDecodingFlyoutChecks();
            UpdateAspectRatioFlyoutChecks();
            UpdateDelayFlyoutLabels();
        }
        else
        {
            PopulateAudioTrackFlyout(audioTracksFlyoutSubItem);
            PopulateEmbeddedSubtitleTrackFlyout(embeddedSubtitleTracksFlyoutSubItem);
            UpdatePlaybackRateFlyoutChecks(playbackRateFlyoutSubItem);
            UpdateHardwareDecodingFlyoutChecks(hardwareDecodingFlyoutSubItem);
            UpdateAspectRatioFlyoutChecks(aspectRatioFlyoutSubItem);
            UpdateDelayFlyoutLabels(subtitleDelayFlyoutSubItem, audioDelayFlyoutSubItem);
            themeToggleMenuItem.IsChecked = ViewModel.IsDarkTheme;
            resumePlaybackToggleItem.IsChecked = ViewModel.Settings.ResumeEnabled;
        exportCurrentSubtitlesFlyoutItem.IsEnabled = _subtitleWorkflowService.Current.Cues.Count > 0;
        }

        return flyout;
    }

    private static MenuFlyoutItem CreateFlyoutItem(string text, RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem
        {
            Text = text
        };
        item.Click += handler;
        return item;
    }

    private MenuFlyoutItem CreateAspectRatioFlyoutItem(string text, string aspectRatio)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            Tag = aspectRatio
        };
        item.Click += AspectRatioFlyoutItem_Click;
        return item;
    }

    private MenuFlyoutItem CreatePlaybackRateFlyoutItem(string text, double rate)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            Tag = rate
        };
        item.Click += PlaybackRateFlyoutItem_Click;
        return item;
    }

    private MenuFlyoutItem CreateHardwareDecodingFlyoutItem(string text, HardwareDecodingMode mode)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            Tag = mode
        };
        item.Click += HardwareDecodingFlyoutItem_Click;
        return item;
    }

    private MenuFlyoutItem CreateTranslationColorFlyoutItem(string text, string colorHex)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Tag = colorHex
        };
        item.Click += TranslationColorFlyoutItem_Click;
        return item;
    }

    private UIElement BuildBrowserPane()
    {
        var grid = new Grid
        {
            RowSpacing = 10
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid
        {
            ColumnSpacing = 10
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Library",
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        var addButton = CreateDrawerActionButton("\uE8B7", "Add Folder", AddRootFolder_Click);
        SetControlHint(addButton, "Add Folder");
        headerActions.Children.Add(addButton);
        var closeButton = CreateDrawerActionButton("\uE711", "Close library", CloseBrowserPane_Click);
        headerActions.Children.Add(closeButton);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        grid.Children.Add(header);

        LibraryTree = new TreeView
        {
            SelectionMode = TreeViewSelectionMode.Single,
            CanDragItems = true
        };
        LibraryTree.Expanding += LibraryTree_Expanding;
        LibraryTree.SelectionChanged += LibraryTree_SelectionChanged;
        LibraryTree.ItemInvoked += LibraryTree_ItemInvoked;
        LibraryTree.DragItemsStarting += LibraryTree_DragItemsStarting;
        LibraryTree.DragItemsCompleted += LibraryTree_DragItemsCompleted;
        Grid.SetRow(LibraryTree, 1);
        grid.Children.Add(LibraryTree);

        return grid;
    }

    private UIElement BuildTimelinePane()
    {
        var seekRow = new Grid
        {
            ColumnSpacing = 12
        };
        seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        CurrentTimeTextBlock = new TextBlock
        {
            Text = "00:00",
            VerticalAlignment = VerticalAlignment.Center
        };
        seekRow.Children.Add(CurrentTimeTextBlock);

        PositionSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        PositionSlider.ValueChanged += PositionSlider_ValueChanged;
        AttachScrubberHandlers(PositionSlider);
        SetControlHint(PositionSlider, "Playback position");
        Grid.SetColumn(PositionSlider, 1);
        seekRow.Children.Add(PositionSlider);

        DurationTextBlock = new TextBlock
        {
            Text = "00:00",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(DurationTextBlock, 2);
        seekRow.Children.Add(DurationTextBlock);
        return seekRow;
    }

    private UIElement BuildPlayerPane(Brush accentBrush)
    {
        PlaybackStage = new Grid
        {
            MinHeight = 180
        };
        PlaybackStage.PointerMoved += PlayerPane_PointerMoved;
        PlaybackStage.SizeChanged += PlaybackStage_SizeChanged;

        VideoStageSurface = new Border
        {
            Background = new SolidColorBrush(Colors.Black),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        VideoStageSurface.SizeChanged += VideoStageSurface_SizeChanged;
        PlaybackStage.Children.Add(VideoStageSurface);

        PlayerHost = new PlaybackHostAdapter(_playbackHostRuntime, _videoPresenter);
        VideoStageSurface.Child = PlayerHost.View;

        DecoderBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(20),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(16),
            Background = accentBrush
        };
        var decoderStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        decoderStack.Children.Add(new FontIcon { Glyph = "\uE956" });
        HardwareDecoderTextBlock = new TextBlock { Text = "mpv ready" };
        decoderStack.Children.Add(HardwareDecoderTextBlock);
        DecoderBadge.Child = decoderStack;
        PlaybackStage.Children.Add(DecoderBadge);

        StatusOverlayBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(20, 20, 20, 0),
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 560,
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(ColorHelper.FromArgb(196, 23, 29, 38))
        };
        StatusOverlayTextBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.White)
        };
        StatusOverlayBorder.Child = StatusOverlayTextBlock;
        PlaybackStage.Children.Add(StatusOverlayBorder);

        SubtitleOverlayBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24),
            Padding = new Thickness(18, 14, 18, 14),
            CornerRadius = new CornerRadius(22),
            MaxWidth = 960,
            Background = new SolidColorBrush(ColorHelper.FromArgb(200, 23, 28, 38))
        };
        var subtitleStack = new StackPanel { Spacing = 8 };
        SourceSubtitleTextBlock = new TextBlock
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = string.Empty,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        subtitleStack.Children.Add(SourceSubtitleTextBlock);
        TranslatedSubtitleTextBlock = new TextBlock
        {
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "Drop a file or choose Open to start playback.",
            TextWrapping = TextWrapping.Wrap
        };
        subtitleStack.Children.Add(TranslatedSubtitleTextBlock);
        SubtitleOverlayBorder.Child = subtitleStack;
        PlaybackStage.Children.Add(SubtitleOverlayBorder);

        UpdatePlaybackStageClip();
        return PlaybackStage;
    }

    private void PlaybackStage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlaybackStageClip();
        PlayerHost?.RequestHostBoundsSync();
        LogPlaybackStageBoundsIfChanged();
    }

    private void VideoStageSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlaybackStageClip();
        PlayerHost?.RequestHostBoundsSync();
    }

    private void UpdatePlaybackStageClip()
    {
        if (PlaybackStage is not null && PlaybackStage.ActualWidth > 0 && PlaybackStage.ActualHeight > 0)
        {
            PlaybackStage.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, PlaybackStage.ActualWidth, PlaybackStage.ActualHeight)
            };
        }

        if (VideoStageSurface is not null && VideoStageSurface.ActualWidth > 0 && VideoStageSurface.ActualHeight > 0)
        {
            VideoStageSurface.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, VideoStageSurface.ActualWidth, VideoStageSurface.ActualHeight)
            };
        }
    }

    private void LogPlaybackStageBoundsIfChanged()
    {
        if (PlaybackStage is null)
        {
            return;
        }

        var width = Math.Round(PlaybackStage.ActualWidth, 1);
        var height = Math.Round(PlaybackStage.ActualHeight, 1);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (Math.Abs(width - _lastLoggedPlaybackStageWidth) < 0.1 &&
            Math.Abs(height - _lastLoggedPlaybackStageHeight) < 0.1)
        {
            return;
        }

        _lastLoggedPlaybackStageWidth = width;
        _lastLoggedPlaybackStageHeight = height;
        _logger.LogInfo(
            "Playback stage bounds updated.",
            BabelLogContext.Create(
                ("stageWidth", width),
                ("stageHeight", height),
                ("surfaceWidth", Math.Round(VideoStageSurface?.ActualWidth ?? 0, 1)),
                ("surfaceHeight", Math.Round(VideoStageSurface?.ActualHeight ?? 0, 1)),
                ("windowMode", _windowModeService.CurrentMode)));
    }

    private UIElement BuildPlaylistPane()
    {
        var grid = new Grid
        {
            RowSpacing = 10
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid
        {
            ColumnSpacing = 10
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerStack = new StackPanel { Spacing = 2 };
        headerStack.Children.Add(new TextBlock
        {
            Text = "Queue",
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        PlaylistSummaryTextBlock = new TextBlock
        {
            Text = "Queue is empty",
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        };
        headerStack.Children.Add(PlaylistSummaryTextBlock);
        header.Children.Add(headerStack);
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        var queueFolderButton = CreateDrawerActionButton("\uE8B7", "Add folder to queue", QueuePlaylistFolder_Click);
        headerActions.Children.Add(queueFolderButton);
        var clearButton = CreateDrawerActionButton("\uE894", "Clear queue", ClearPlaylist_Click);
        headerActions.Children.Add(clearButton);
        var closeButton = CreateDrawerActionButton("\uE711", "Close queue", ClosePlaylistPane_Click);
        headerActions.Children.Add(closeButton);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        grid.Children.Add(header);

        var nowPlayingBorder = CreatePanelBorder(
            new SolidColorBrush(ColorHelper.FromArgb(32, 255, 255, 255)),
            new SolidColorBrush(ColorHelper.FromArgb(56, 255, 255, 255)),
            1,
            new CornerRadius(18),
            new Thickness(14, 12, 14, 12));
        Grid.SetRow(nowPlayingBorder, 1);
        var nowPlayingStack = new StackPanel
        {
            Spacing = 4
        };
        nowPlayingStack.Children.Add(new TextBlock
        {
            Text = "Now Playing",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        NowPlayingQueueTextBlock = new TextBlock
        {
            Text = "Nothing is playing.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 209, 219, 233))
        };
        nowPlayingStack.Children.Add(NowPlayingQueueTextBlock);
        nowPlayingBorder.Child = nowPlayingStack;
        grid.Children.Add(nowPlayingBorder);

        var queueHeader = new TextBlock
        {
            Text = "Queue",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        Grid.SetRow(queueHeader, 2);
        grid.Children.Add(queueHeader);

        PlaylistList = new ListView
        {
            DisplayMemberPath = nameof(PlaylistItem.DisplayName),
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.Single
        };
        PlaylistList.ItemClick += PlaylistList_ItemClick;
        PlaylistList.SelectionChanged += PlaylistList_SelectionChanged;
        Grid.SetRow(PlaylistList, 3);
        grid.Children.Add(PlaylistList);

        var historyHeader = new TextBlock
        {
            Text = "Recent",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        Grid.SetRow(historyHeader, 4);
        grid.Children.Add(historyHeader);

        HistoryList = new ListView
        {
            DisplayMemberPath = nameof(PlaylistItem.DisplayName),
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.Single
        };
        HistoryList.ItemClick += HistoryList_ItemClick;
        HistoryList.SelectionChanged += HistoryList_SelectionChanged;
        Grid.SetRow(HistoryList, 5);
        grid.Children.Add(HistoryList);

        return grid;
    }

    private UIElement BuildTransportPane()
    {
        var grid = new Grid
        {
            RowSpacing = 6
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var transportHost = new Grid();
        transportHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        transportHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transportHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var transportButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(transportButtons, 1);
        transportButtons.Children.Add(CreateTransportButton(new SymbolIcon(Symbol.Previous), "Previous track", PreviousTrack_Click));
        PlayPauseButton = CreateTransportButton(new SymbolIcon(Symbol.Play), "Play", PlayPauseButton_Click, emphasized: true);
        transportButtons.Children.Add(PlayPauseButton);
        transportButtons.Children.Add(CreateTransportButton(new SymbolIcon(Symbol.Next), "Next track", NextTrack_Click));
        transportHost.Children.Add(transportButtons);
        grid.Children.Add(transportHost);

        var secondaryRow = new Grid
        {
            ColumnSpacing = 12
        };
        secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(secondaryRow, 1);

        var secondaryButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(secondaryButtons, 1);
        secondaryRow.Children.Add(secondaryButtons);

        var volumeCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        VolumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 80,
            StepFrequency = 1,
            SmallChange = 2,
            LargeChange = 10,
            Width = 130
        };
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        SetControlHint(VolumeSlider, "Volume");
        volumeCluster.Children.Add(VolumeSlider);

        MuteToggleButton = new ToggleButton();
        MuteToggleButton.Click += MuteToggleButton_Click;
        SetControlHint(MuteToggleButton, "Mute");
        volumeCluster.Children.Add(MuteToggleButton);
        Grid.SetColumn(volumeCluster, 2);
        secondaryRow.Children.Add(volumeCluster);
        grid.Children.Add(secondaryRow);

        return grid;
    }

    private FrameworkElement BuildLanguageToolsDrawer()
    {
        var drawer = new StackPanel
        {
            Spacing = 8
        };

        LanguageToolsToggleButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 2, 0, 2),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        LanguageToolsToggleButton.Click += LanguageToolsToggleButton_Click;
        SetControlHint(LanguageToolsToggleButton, "Toggle language tools");
        drawer.Children.Add(LanguageToolsToggleButton);

        LanguageToolsContentBorder = CreatePanelBorder(
            new SolidColorBrush(ColorHelper.FromArgb(16, 255, 255, 255)),
            new SolidColorBrush(ColorHelper.FromArgb(20, 255, 255, 255)),
            1,
            new CornerRadius(16),
            new Thickness(10, 8, 10, 8));
        LanguageToolsContentBorder.SizeChanged += LanguageToolsContentBorder_SizeChanged;

        LanguageToolsGroupsGrid = new Grid
        {
            RowSpacing = 10,
            ColumnSpacing = 16
        };

        var processingControls = new StackPanel
        {
            Spacing = 8
        };
        TranscriptionModelComboBox = new ComboBox
        {
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        TranscriptionModelComboBox.ItemsSource = Array.Empty<TranscriptionModelSelection>();
        TranscriptionModelComboBox.DisplayMemberPath = nameof(TranscriptionModelSelection.DisplayName);
        TranscriptionModelComboBox.SelectionChanged += TranscriptionModelComboBox_SelectionChanged;
        SetControlHint(TranscriptionModelComboBox, "Transcription model");
        processingControls.Children.Add(CreateCompactLanguageToolsRow("Transcription Model", TranscriptionModelComboBox));

        TranslationToggleSwitch = new ToggleSwitch
        {
            OffContent = "Off",
            OnContent = "On"
        };
        TranslationToggleSwitch.Toggled += TranslationToggleSwitch_Toggled;
        SetControlHint(TranslationToggleSwitch, "Translate current video");
        processingControls.Children.Add(CreateCompactLanguageToolsRow("Translate Video", TranslationToggleSwitch));

        AutoTranslateToggleSwitch = new ToggleSwitch
        {
            OffContent = "Off",
            OnContent = "On"
        };
        AutoTranslateToggleSwitch.Toggled += AutoTranslateToggleSwitch_Toggled;
        SetControlHint(AutoTranslateToggleSwitch, "Auto translate non-English");
        processingControls.Children.Add(CreateCompactLanguageToolsRow("Auto Translate", AutoTranslateToggleSwitch));

        TranslationModelComboBox = new ComboBox
        {
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        TranslationModelComboBox.ItemsSource = Array.Empty<TranslationModelSelection>();
        TranslationModelComboBox.DisplayMemberPath = nameof(TranslationModelSelection.DisplayName);
        TranslationModelComboBox.SelectionChanged += TranslationModelComboBox_SelectionChanged;
        TranslationModelComboBox.IsEnabled = false;
        SetControlHint(TranslationModelComboBox, "Translation model");
        processingControls.Children.Add(CreateCompactLanguageToolsRow("Translation Model", TranslationModelComboBox));
        LanguageToolsTranscriptionGroup = CreateLanguageToolsSection(
            "Language Processing",
            processingControls);
        LanguageToolsTranslationGroup = new Border
        {
            Visibility = Visibility.Collapsed
        };

        var subtitleControls = new StackPanel
        {
            Spacing = 8
        };
        var subtitleStatusRow = new Grid
        {
            ColumnSpacing = 10
        };
        subtitleStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subtitleStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subtitleStatusRow.Children.Add(new TextBlock
        {
            Text = "Subtitles",
            VerticalAlignment = VerticalAlignment.Center
        });
        LanguageToolsSubtitleStatusTextBlock = new TextBlock
        {
            MaxWidth = 90,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0)
        };
        LanguageToolsSubtitleToggleButton = new ToggleButton
        {
            MinWidth = 90,
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        LanguageToolsSubtitleToggleButton.Click += SubtitleVisibilityToggleButton_Click;
        SetControlHint(LanguageToolsSubtitleToggleButton, "Toggle subtitles");
        var subtitleStatusActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        subtitleStatusActions.Children.Add(LanguageToolsSubtitleStatusTextBlock);
        subtitleStatusActions.Children.Add(LanguageToolsSubtitleToggleButton);
        Grid.SetColumn(subtitleStatusActions, 1);
        subtitleStatusRow.Children.Add(subtitleStatusActions);
        subtitleControls.Children.Add(subtitleStatusRow);

        SubtitleModeComboBox = new ComboBox
        {
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        SubtitleModeComboBox.Items.Add(new ComboBoxItem { Content = "Off", Tag = SubtitleRenderMode.Off });
        SubtitleModeComboBox.Items.Add(new ComboBoxItem { Content = "Source Only", Tag = SubtitleRenderMode.SourceOnly });
        SubtitleModeComboBox.Items.Add(new ComboBoxItem { Content = "Translation Only", Tag = SubtitleRenderMode.TranslationOnly });
        SubtitleModeComboBox.Items.Add(new ComboBoxItem { Content = "Dual", Tag = SubtitleRenderMode.Dual });
        SubtitleModeComboBox.SelectionChanged += SubtitleModeComboBox_SelectionChanged;
        SetControlHint(SubtitleModeComboBox, "Subtitle mode");
        subtitleControls.Children.Add(CreateCompactLanguageToolsRow("Subtitle Mode", SubtitleModeComboBox));

        var subtitleStyleButton = new DropDownButton
        {
            Content = "Style",
            Flyout = CreateSubtitleStyleFlyout(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        SetControlHint(subtitleStyleButton, "Subtitle style");
        subtitleControls.Children.Add(CreateCompactLanguageToolsRow("Subtitle Style", subtitleStyleButton));

        var importSubsButton = new Button
        {
            Content = "Import Subs",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        importSubsButton.Click += ImportSubtitle_Click;
        SetControlHint(importSubsButton, "Import subtitle file");
        subtitleControls.Children.Add(CreateCompactLanguageToolsRow("Import", importSubsButton));

        var subtitleDelayPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        var subtitleDelayBackButton = new Button
        {
            Content = "\u2212",
            MinWidth = 32,
            Padding = new Thickness(6, 4, 6, 4)
        };
        subtitleDelayBackButton.Click += SubtitleDelayBack_Click;
        SubtitleDelayValueText = new TextBlock
        {
            Text = "0.00s",
            MinWidth = 52,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var subtitleDelayForwardButton = new Button
        {
            Content = "+",
            MinWidth = 32,
            Padding = new Thickness(6, 4, 6, 4)
        };
        subtitleDelayForwardButton.Click += SubtitleDelayForward_Click;
        var subtitleDelayResetButton = new Button
        {
            Content = "Reset",
            Padding = new Thickness(8, 4, 8, 4)
        };
        subtitleDelayResetButton.Click += ResetSubtitleDelay_Click;
        subtitleDelayPanel.Children.Add(subtitleDelayBackButton);
        subtitleDelayPanel.Children.Add(SubtitleDelayValueText);
        subtitleDelayPanel.Children.Add(subtitleDelayForwardButton);
        subtitleDelayPanel.Children.Add(subtitleDelayResetButton);
        subtitleControls.Children.Add(CreateCompactLanguageToolsRow("Subtitle Delay", subtitleDelayPanel));

        var playbackSpeedPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        PlaybackSpeedSlider = new Slider
        {
            Minimum = 0.25,
            Maximum = 2.0,
            StepFrequency = 0.25,
            SmallChange = 0.25,
            Value = 1.0,
            Width = 120
        };
        PlaybackSpeedSlider.ValueChanged += PlaybackSpeedSlider_ValueChanged;
        PlaybackSpeedValueText = new TextBlock
        {
            Text = "1.00x",
            MinWidth = 40,
            VerticalAlignment = VerticalAlignment.Center
        };
        playbackSpeedPanel.Children.Add(PlaybackSpeedSlider);
        playbackSpeedPanel.Children.Add(PlaybackSpeedValueText);
        subtitleControls.Children.Add(CreateCompactLanguageToolsRow("Playback Speed", playbackSpeedPanel));

        LanguageToolsSubtitlesGroup = CreateLanguageToolsSection(
            "Subtitle Display",
            subtitleControls);

        LanguageToolsGroupsGrid.Children.Add(LanguageToolsTranscriptionGroup);
        LanguageToolsGroupsGrid.Children.Add(LanguageToolsSubtitlesGroup);

        LanguageToolsContentBorder.Child = LanguageToolsGroupsGrid;
        drawer.Children.Add(LanguageToolsContentBorder);
        UpdateLanguageToolsDrawerState();
        UpdateLanguageToolsResponsiveLayout();
        return drawer;
    }

    private void LanguageToolsContentBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyLanguageToolsResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateLanguageToolsResponsiveLayout()
    {
        if (LanguageToolsContentBorder is null)
        {
            return;
        }

        var availableWidth = LanguageToolsContentBorder.ActualWidth;
        if (availableWidth <= 0 && LanguageToolsPane is not null)
        {
            availableWidth = Math.Max(
                0,
                LanguageToolsPane.ActualWidth - LanguageToolsPane.Padding.Left - LanguageToolsPane.Padding.Right);
        }

        ApplyLanguageToolsResponsiveLayout(availableWidth);
    }

    private void ApplyLanguageToolsResponsiveLayout(double availableWidth)
    {
        if (LanguageToolsGroupsGrid is null
            || LanguageToolsTranscriptionGroup is null
            || LanguageToolsSubtitlesGroup is null
            || availableWidth <= 0)
        {
            return;
        }

        var layoutMode = GetLanguageToolsPanelLayoutMode(availableWidth);
        if (_languageToolsPanelLayoutMode == layoutMode)
        {
            return;
        }

        _languageToolsPanelLayoutMode = layoutMode;
        LanguageToolsGroupsGrid.ColumnDefinitions.Clear();
        LanguageToolsGroupsGrid.RowDefinitions.Clear();

        switch (layoutMode)
        {
            case LanguageToolsPanelLayoutMode.Wide:
                LanguageToolsGroupsGrid.ColumnSpacing = 16;
                LanguageToolsGroupsGrid.RowSpacing = 0;
                LanguageToolsGroupsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LanguageToolsGroupsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LanguageToolsGroupsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                PlaceLanguageToolsGroup(LanguageToolsTranscriptionGroup, row: 0, column: 0);
                PlaceLanguageToolsGroup(LanguageToolsSubtitlesGroup, row: 0, column: 1);
                break;

            case LanguageToolsPanelLayoutMode.Medium:
                LanguageToolsGroupsGrid.ColumnSpacing = 12;
                LanguageToolsGroupsGrid.RowSpacing = 0;
                LanguageToolsGroupsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LanguageToolsGroupsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LanguageToolsGroupsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                PlaceLanguageToolsGroup(LanguageToolsTranscriptionGroup, row: 0, column: 0);
                PlaceLanguageToolsGroup(LanguageToolsSubtitlesGroup, row: 0, column: 1);
                break;

            default:
                LanguageToolsGroupsGrid.ColumnSpacing = 0;
                LanguageToolsGroupsGrid.RowSpacing = 8;
                LanguageToolsGroupsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LanguageToolsGroupsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                LanguageToolsGroupsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                PlaceLanguageToolsGroup(LanguageToolsTranscriptionGroup, row: 0, column: 0);
                PlaceLanguageToolsGroup(LanguageToolsSubtitlesGroup, row: 1, column: 0);
                break;
        }
    }

    private static LanguageToolsPanelLayoutMode GetLanguageToolsPanelLayoutMode(double availableWidth)
        => availableWidth switch
        {
            >= 920 => LanguageToolsPanelLayoutMode.Wide,
            >= 680 => LanguageToolsPanelLayoutMode.Medium,
            _ => LanguageToolsPanelLayoutMode.Narrow
        };

    private static void PlaceLanguageToolsGroup(FrameworkElement element, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private static UIElement CreateCompactLanguageToolsRow(string label, FrameworkElement control)
    {
        var row = new Grid
        {
            ColumnSpacing = 12
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        });

        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static Border CreateLanguageToolsSection(string title, UIElement content)
    {
        var body = new StackPanel
        {
            Spacing = 6
        };
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 209, 219, 233))
        });
        body.Children.Add(content);

        return new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(8, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(18, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            Child = body
        };
    }

    private Flyout CreateSubtitleStyleFlyout()
    {
        var content = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(14),
            MaxWidth = 340
        };
        content.Children.Add(new TextBlock
        {
            Text = "Subtitle Style",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 16
        });
        content.Children.Add(CreateStyleActionRow(
            "Text Size",
            CreateStyleActionButton("Larger", IncreaseSubtitleFont_Click),
            CreateStyleActionButton("Smaller", DecreaseSubtitleFont_Click)));
        content.Children.Add(CreateStyleActionRow(
            "Background",
            CreateStyleActionButton("More", IncreaseSubtitleBackground_Click),
            CreateStyleActionButton("Less", DecreaseSubtitleBackground_Click)));
        content.Children.Add(CreateStyleActionRow(
            "Position",
            CreateStyleActionButton("Raise", RaiseSubtitles_Click),
            CreateStyleActionButton("Lower", LowerSubtitles_Click)));
        content.Children.Add(CreateStyleActionRow(
            "Translation Color",
            CreateStyleActionButton("White", TranslationColorFlyoutItem_Click, "#FFFFFF"),
            CreateStyleActionButton("Amber", TranslationColorFlyoutItem_Click, "#FFD580"),
            CreateStyleActionButton("Cyan", TranslationColorFlyoutItem_Click, "#BFEFFF")));

        return new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.TopEdgeAlignedRight
        };
    }

    private MenuFlyout CreateSubtitleModeFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateSubtitleModeFlyoutItem("Off", SubtitleRenderMode.Off));
        flyout.Items.Add(CreateSubtitleModeFlyoutItem("Source Only", SubtitleRenderMode.SourceOnly));
        flyout.Items.Add(CreateSubtitleModeFlyoutItem("Translation Only", SubtitleRenderMode.TranslationOnly));
        flyout.Items.Add(CreateSubtitleModeFlyoutItem("Dual", SubtitleRenderMode.Dual));
        return flyout;
    }

    private MenuFlyoutItem CreateSubtitleModeFlyoutItem(string label, SubtitleRenderMode mode)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = label,
            Tag = mode,
            IsChecked = GetEffectiveSubtitleRenderMode() == mode
        };
        item.Click += SubtitleRenderModeFlyoutItem_Click;
        return item;
    }

    private static StackPanel CreateStyleActionRow(string label, params Button[] buttons)
    {
        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        foreach (var button in buttons)
        {
            actions.Children.Add(button);
        }

        row.Children.Add(actions);
        return row;
    }

    private static Button CreateStyleActionButton(string label, RoutedEventHandler handler, object? tag = null)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 72,
            Tag = tag
        };
        button.Click += handler;
        SetControlHint(button, label);
        return button;
    }

    private static Button CreateTransportButton(IconElement icon, string label, RoutedEventHandler handler, bool emphasized = false)
    {
        var button = new Button
        {
            Content = icon,
            MinWidth = emphasized ? 88 : 60,
            MinHeight = emphasized ? 60 : 50,
            Padding = emphasized ? new Thickness(22, 14, 22, 14) : new Thickness(14, 12, 14, 12)
        };
        button.Click += handler;
        SetControlHint(button, label);
        return button;
    }

    private static void SetControlHint(FrameworkElement element, string label)
    {
        ToolTipService.SetToolTip(element, label);
        AutomationProperties.SetName(element, label);
    }
}

