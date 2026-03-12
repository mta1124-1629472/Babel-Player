using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;
using Windows.System;
using Windows.UI;

namespace BabelPlayer.WinUI;

public sealed partial class MainWindow : Window
{
    private const string IdleWindowSubtitle = "Play anything. Understand everything.";
    private const string LibraryQueueDragFormat = "BabelPlayer/LibraryQueuePaths";
    private readonly SettingsFacade _settingsFacade = new();
    private readonly LibraryBrowserService _libraryBrowserService = new();
    private readonly PlaybackQueueController _playbackQueueController = new();
    private readonly CredentialFacade _credentialFacade = new();
    private readonly SubtitleWorkflowController _subtitleWorkflowController;
    private readonly IPlaybackBackend _playbackBackend;
    private readonly PlaybackBackendCoordinator _playbackBackendCoordinator;
    private readonly ShellProjectionService _shellProjectionService;
    private readonly ShellController _shellController;
    private readonly IVideoPresenter _videoPresenter;
    private readonly ISubtitlePresenter _subtitlePresenter;
    private readonly ShortcutService _shortcutService = new();
    private readonly IFilePickerService _filePickerService;
    private readonly WinUIWindowModeService _windowModeService;
    private readonly WinUICredentialDialogService _credentialDialogService;
    private readonly IRuntimeBootstrapService _runtimeBootstrapService;
    private readonly StageCoordinator _stageCoordinator;
    private readonly List<MediaTrackInfo> _currentTracks = [];
    private bool _suppressPositionSliderChanges;
    private bool _suppressFullscreenSliderChanges;
    private bool _suppressWorkflowControlEvents;
    private bool _suppressWindowModeButtonChanges;
    private bool _isPositionScrubbing;
    private bool _isInitializingShellState;
    private bool _isNormalizingWinUITranscriptionSelection;
    private bool _isLanguageToolsExpanded = true;
    private bool _autoCollapsedLanguageToolsForPortraitVideo;
    private bool _isLanguageToolsAutoCollapseOverridden;
    private bool _hasAttemptedSystemBackdrop;
    private bool _shellDropTargetsInitialized;
    private bool _isLibraryDragOperationInProgress;
    private Slider? _activeScrubber;
    private string? _pendingAutoFitPath;
    private string? _lastAutoFitSignature;
    private string? _pendingLibraryLoadPath;
    private string? _subtitleSourceOnlyOverrideVideoPath;
    private LibraryNode? _selectedLibraryNode;
    private SubtitleRenderMode _lastNonOffSubtitleRenderMode = SubtitleRenderMode.TranslationOnly;
    private string _selectedAspectRatio = "auto";
    private double _audioDelaySeconds;
    private double _subtitleDelaySeconds;
    private MicaBackdrop? _micaBackdrop;
    private readonly Dictionary<string, ResolvedShortcutBinding> _resolvedShortcutBindings = new(StringComparer.OrdinalIgnoreCase);
    private Border AppTitleBar = null!;
    private TextBlock WindowTitleTextBlock = null!;
    private TextBlock WindowSubtitleTextBlock = null!;
    private CommandBar ShellCommandBar = null!;
    private AppBarButton PlaybackOptionsButton = null!;
    private MenuFlyout PlaybackOptionsFlyout = null!;
    private ToggleMenuFlyoutItem ThemeToggleMenuItem = null!;
    private AppBarToggleButton SubtitleVisibilityToggleButton = null!;
    private AppBarToggleButton BrowserPaneToggle = null!;
    private AppBarToggleButton PlaylistPaneToggle = null!;
    private AppBarToggleButton ImmersiveToggleButton = null!;
    private AppBarToggleButton FullscreenToggleButton = null!;
    private AppBarToggleButton PictureInPictureToggleButton = null!;
    private InfoBar StatusInfoBar = null!;
    private Grid ShellContentGrid = null!;
    private Grid CenterStageGrid = null!;
    private Border BrowserPane = null!;
    private Border PlaylistPane = null!;
    private Border PlayerPane = null!;
    private Border TimelinePane = null!;
    private Border TransportPane = null!;
    private Border LanguageToolsPane = null!;
    private Border LanguageToolsContentBorder = null!;
    private Border DecoderBadge = null!;
    private Border SubtitleOverlayBorder = null!;
    private ColumnDefinition BrowserColumn = null!;
    private ColumnDefinition PlaylistColumn = null!;
    private TreeView LibraryTree = null!;
    private PlaybackHostAdapter PlayerHost = null!;
    private TextBlock HardwareDecoderTextBlock = null!;
    private TextBlock SourceSubtitleTextBlock = null!;
    private TextBlock TranslatedSubtitleTextBlock = null!;
    private TextBlock PlaylistSummaryTextBlock = null!;
    private TextBlock NowPlayingQueueTextBlock = null!;
    private ListView PlaylistList = null!;
    private ListView HistoryList = null!;
    private Button PlayPauseButton = null!;
    private Slider PositionSlider = null!;
    private TextBlock CurrentTimeTextBlock = null!;
    private TextBlock DurationTextBlock = null!;
    private Slider VolumeSlider = null!;
    private ToggleButton MuteToggleButton = null!;
    private Button LanguageToolsToggleButton = null!;
    private ToggleButton LanguageToolsSubtitleToggleButton = null!;
    private ComboBox SubtitleModeComboBox = null!;
    private ComboBox TranscriptionModelComboBox = null!;
    private ComboBox TranslationModelComboBox = null!;
    private ToggleSwitch TranslationToggleSwitch = null!;
    private ToggleSwitch AutoTranslateToggleSwitch = null!;
    private MenuFlyoutSubItem PlaybackRateFlyoutSubItem = null!;
    private MenuFlyoutSubItem HardwareDecodingFlyoutSubItem = null!;
    private MenuFlyoutSubItem AspectRatioFlyoutSubItem = null!;
    private MenuFlyoutSubItem AudioTracksFlyoutSubItem = null!;
    private MenuFlyoutSubItem EmbeddedSubtitleTracksFlyoutSubItem = null!;
    private MenuFlyoutSubItem SubtitleDelayFlyoutSubItem = null!;
    private MenuFlyoutSubItem AudioDelayFlyoutSubItem = null!;
    private ToggleMenuFlyoutItem ResumePlaybackToggleItem = null!;
    private MenuFlyoutItem ExportCurrentSubtitlesFlyoutItem = null!;
    private Button OverlayPlayPauseButton = null!;
    private Button OverlaySubtitleToggleButton = null!;
    private Slider FullscreenPositionSlider = null!;
    private TextBlock FullscreenCurrentTimeTextBlock = null!;
    private TextBlock FullscreenDurationTextBlock = null!;
    private Grid RootGrid = null!;

    public MainShellViewModel ViewModel { get; } = new();

    public MainWindow(IShellCompositionRoot? compositionRoot = null)
    {
        // Removed: InitializeComponent(); (not needed for code-built UI in WinUI 3)

        RootGrid = new Grid();
        Content = RootGrid;
        var dependencies = (compositionRoot ?? new ShellCompositionRoot()).Create(
            this,
            RootGrid,
            _playbackQueueController,
            _credentialFacade,
            SuppressDialogPresentation);
        _filePickerService = dependencies.FilePickerService;
        _windowModeService = dependencies.WindowModeService;
        _credentialDialogService = dependencies.CredentialDialogService;
        _runtimeBootstrapService = dependencies.RuntimeBootstrapService;
        _subtitleWorkflowController = dependencies.SubtitleWorkflowController;
        _playbackBackend = dependencies.PlaybackBackend;
        _playbackBackendCoordinator = dependencies.PlaybackBackendCoordinator;
        _videoPresenter = dependencies.VideoPresenter;
        _subtitlePresenter = dependencies.SubtitlePresenter;
        _shellProjectionService = dependencies.ShellProjectionService;
        _shellController = dependencies.ShellController;
        BuildShell();
        _stageCoordinator = dependencies.StageCoordinator;

        var fullscreenExitAccelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Escape
        };
        fullscreenExitAccelerator.Invoked += FullscreenExitAccelerator_Invoked;
        RootGrid.KeyboardAccelerators.Add(fullscreenExitAccelerator);
        RootGrid.KeyDown += RootGrid_KeyDown;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        PlayerHost.Initialize(this);
        PlaylistList.ItemsSource = ViewModel.Queue.QueueItems;
        HistoryList.ItemsSource = ViewModel.Queue.HistoryItems;

        PlayerHost.MediaOpened += PlayerHost_MediaOpened;
        PlayerHost.MediaEnded += PlayerHost_MediaEnded;
        PlayerHost.MediaFailed += PlayerHost_MediaFailed;
        PlayerHost.RuntimeInstallProgress += PlayerHost_RuntimeInstallProgress;
        PlayerHost.PlaybackStateChanged += PlayerHost_PlaybackStateChanged;
        PlayerHost.InputActivity += PlayerHost_InputActivity;
        PlayerHost.FullscreenExitRequested += PlayerHost_FullscreenExitRequested;
        PlayerHost.ShortcutKeyPressed += PlayerHost_ShortcutKeyPressed;
        _subtitleWorkflowController.StatusChanged += SubtitleWorkflowController_StatusChanged;
        _subtitleWorkflowController.SnapshotChanged += SubtitleWorkflowController_SnapshotChanged;
        _shellProjectionService.ProjectionChanged += ShellProjectionService_ProjectionChanged;
        _shellController.QueueSnapshotChanged += ShellController_QueueSnapshotChanged;

        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;

        InitializeShellState();
        ApplyShellProjection(_shellProjectionService.Current);
        ApplyQueueSnapshot(_shellController.QueueSnapshot);
        _ = _subtitleWorkflowController.InitializeAsync();
    }

    private void BuildShell()
    {
        RootGrid ??= new Grid();
        Content = RootGrid;

        RootGrid.RowDefinitions.Clear();
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var shellBrush = new SolidColorBrush(ColorHelper.FromArgb(188, 17, 20, 27));
        var drawerBrush = new SolidColorBrush(ColorHelper.FromArgb(136, 23, 29, 38));
        var railBrush = new SolidColorBrush(ColorHelper.FromArgb(110, 20, 25, 34));
        var playerSurfaceBrush = new SolidColorBrush(ColorHelper.FromArgb(72, 12, 16, 22));
        var subtleBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(72, 120, 136, 160));
        var stageBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(126, 106, 126, 156));
        var accentBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 47, 111, 178));

        AppTitleBar = new Border
        {
            Padding = new Thickness(22, 12, 22, 12),
            Background = shellBrush
        };
        Grid.SetRow(AppTitleBar, 0);

        var titleBarGrid = new Grid
        {
            ColumnSpacing = 12
        };
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBarGrid.Children.Add(new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Source = new BitmapImage(new Uri("ms-appx:///BabelPlayer.ico"))
        });

        var titleStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleStack, 1);
        WindowTitleTextBlock = new TextBlock
        {
            Text = "Babel Player",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        titleStack.Children.Add(WindowTitleTextBlock);
        WindowSubtitleTextBlock = new TextBlock
        {
            Text = IdleWindowSubtitle,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        };
        titleStack.Children.Add(WindowSubtitleTextBlock);
        titleBarGrid.Children.Add(titleStack);
        AppTitleBar.Child = titleBarGrid;
        RootGrid.Children.Add(AppTitleBar);

        ShellCommandBar = new CommandBar
        {
            Margin = new Thickness(20, 8, 20, 0),
            Background = shellBrush,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right
        };
        Grid.SetRow(ShellCommandBar, 1);
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

        PlaybackOptionsFlyout = BuildPlaybackOptionsFlyout();
        PlaybackOptionsButton = new AppBarButton
        {
            Label = "Settings",
            Icon = new SymbolIcon(Symbol.Setting),
            Flyout = PlaybackOptionsFlyout
        };
        SetControlHint(PlaybackOptionsButton, "Settings");
        ShellCommandBar.PrimaryCommands.Add(PlaybackOptionsButton);
        RootGrid.Children.Add(ShellCommandBar);

        StatusInfoBar = new InfoBar
        {
            Margin = new Thickness(16, 12, 16, 0),
            IsClosable = true,
            IsOpen = false,
            Message = string.Empty,
            Severity = InfoBarSeverity.Informational
        };
        Grid.SetRow(StatusInfoBar, 2);
        RootGrid.Children.Add(StatusInfoBar);

        ShellContentGrid = new Grid
        {
            Padding = new Thickness(20, 10, 20, 18),
            ColumnSpacing = 0
        };
        Grid.SetRow(ShellContentGrid, 3);
        BrowserColumn = new ColumnDefinition { Width = new GridLength(0) };
        PlaylistColumn = new ColumnDefinition { Width = new GridLength(0) };
        ShellContentGrid.ColumnDefinitions.Add(BrowserColumn);
        ShellContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ShellContentGrid.ColumnDefinitions.Add(PlaylistColumn);
        RootGrid.Children.Add(ShellContentGrid);

        BrowserPane = CreatePanelBorder(drawerBrush, subtleBorderBrush, 1, new CornerRadius(28), new Thickness(18, 16, 18, 18));
        BrowserPane.Visibility = Visibility.Collapsed;
        Grid.SetColumn(BrowserPane, 0);
        ShellContentGrid.Children.Add(BrowserPane);
        BrowserPane.Child = BuildBrowserPane();

        CenterStageGrid = new Grid
        {
            RowSpacing = 10
        };
        CenterStageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        CenterStageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        CenterStageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        CenterStageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(CenterStageGrid, 1);
        ShellContentGrid.Children.Add(CenterStageGrid);

        PlayerPane = CreatePanelBorder(playerSurfaceBrush, stageBorderBrush, 1, new CornerRadius(26), new Thickness(12));
        PlayerPane.MinHeight = 320;
        Grid.SetColumn(PlayerPane, 1);
        CenterStageGrid.Children.Add(PlayerPane);
        PlayerPane.Child = BuildPlayerPane(accentBrush);

        TimelinePane = CreatePanelBorder(railBrush, subtleBorderBrush, 0, new CornerRadius(18), new Thickness(14, 10, 14, 10));
        Grid.SetRow(TimelinePane, 1);
        TimelinePane.Child = BuildTimelinePane();
        CenterStageGrid.Children.Add(TimelinePane);

        TransportPane = CreatePanelBorder(railBrush, subtleBorderBrush, 0, new CornerRadius(22), new Thickness(10, 6, 10, 6));
        TransportPane.Margin = new Thickness(0);
        Grid.SetRow(TransportPane, 2);
        TransportPane.Child = BuildTransportPane();
        CenterStageGrid.Children.Add(TransportPane);

        LanguageToolsPane = CreatePanelBorder(drawerBrush, subtleBorderBrush, 0, new CornerRadius(22), new Thickness(16, 14, 16, 16));
        Grid.SetRow(LanguageToolsPane, 3);
        LanguageToolsPane.Child = BuildLanguageToolsDrawer();
        CenterStageGrid.Children.Add(LanguageToolsPane);

        PlaylistPane = CreatePanelBorder(drawerBrush, subtleBorderBrush, 1, new CornerRadius(28), new Thickness(18, 16, 18, 18));
        PlaylistPane.Visibility = Visibility.Collapsed;
        Grid.SetColumn(PlaylistPane, 2);
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

    private MenuFlyout BuildPlaybackOptionsFlyout()
    {
        var flyout = new MenuFlyout();

        ThemeToggleMenuItem = new ToggleMenuFlyoutItem
        {
            Text = "Dark Theme"
        };
        ThemeToggleMenuItem.Click += ThemeToggleMenuItem_Click;
        flyout.Items.Add(ThemeToggleMenuItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        PlaybackRateFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Playback Rate"
        };
        PlaybackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("0.75x", 0.75));
        PlaybackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("1.00x", 1.00));
        PlaybackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("1.25x", 1.25));
        PlaybackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("1.50x", 1.50));
        PlaybackRateFlyoutSubItem.Items.Add(CreatePlaybackRateFlyoutItem("2.00x", 2.00));
        flyout.Items.Add(PlaybackRateFlyoutSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        HardwareDecodingFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Hardware Decode"
        };
        HardwareDecodingFlyoutSubItem.Items.Add(CreateHardwareDecodingFlyoutItem("Auto Safe", HardwareDecodingMode.AutoSafe));
        HardwareDecodingFlyoutSubItem.Items.Add(CreateHardwareDecodingFlyoutItem("D3D11", HardwareDecodingMode.D3D11));
        HardwareDecodingFlyoutSubItem.Items.Add(CreateHardwareDecodingFlyoutItem("NVDEC", HardwareDecodingMode.Nvdec));
        HardwareDecodingFlyoutSubItem.Items.Add(CreateHardwareDecodingFlyoutItem("Software", HardwareDecodingMode.Software));
        flyout.Items.Add(HardwareDecodingFlyoutSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        AspectRatioFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Aspect Ratio"
        };
        AspectRatioFlyoutSubItem.Items.Add(CreateAspectRatioFlyoutItem("Auto", "auto"));
        AspectRatioFlyoutSubItem.Items.Add(CreateAspectRatioFlyoutItem("16:9", "16:9"));
        AspectRatioFlyoutSubItem.Items.Add(CreateAspectRatioFlyoutItem("4:3", "4:3"));
        AspectRatioFlyoutSubItem.Items.Add(CreateAspectRatioFlyoutItem("Fill", "-1"));
        flyout.Items.Add(AspectRatioFlyoutSubItem);

        AudioTracksFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Audio Track"
        };
        flyout.Items.Add(AudioTracksFlyoutSubItem);

        EmbeddedSubtitleTracksFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Embedded Subtitles"
        };
        flyout.Items.Add(EmbeddedSubtitleTracksFlyoutSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        SubtitleDelayFlyoutSubItem = new MenuFlyoutSubItem();
        SubtitleDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Back 50 ms", SubtitleDelayBack_Click));
        SubtitleDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Forward 50 ms", SubtitleDelayForward_Click));
        SubtitleDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Reset", ResetSubtitleDelay_Click));
        flyout.Items.Add(SubtitleDelayFlyoutSubItem);

        AudioDelayFlyoutSubItem = new MenuFlyoutSubItem();
        AudioDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Back 50 ms", AudioDelayBack_Click));
        AudioDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Forward 50 ms", AudioDelayForward_Click));
        AudioDelayFlyoutSubItem.Items.Add(CreateFlyoutItem("Reset", ResetAudioDelay_Click));
        flyout.Items.Add(AudioDelayFlyoutSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        ResumePlaybackToggleItem = new ToggleMenuFlyoutItem
        {
            Text = "Resume Playback"
        };
        ResumePlaybackToggleItem.Click += ResumePlaybackToggleItem_Click;
        flyout.Items.Add(ResumePlaybackToggleItem);

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Keyboard Shortcuts..."
        });
        ((MenuFlyoutItem)flyout.Items[^1]).Click += EditShortcuts_Click;

        ExportCurrentSubtitlesFlyoutItem = new MenuFlyoutItem
        {
            Text = "Export Current Subtitles",
            IsEnabled = false
        };
        ExportCurrentSubtitlesFlyoutItem.Click += ExportCurrentSubtitles_Click;
        flyout.Items.Add(ExportCurrentSubtitlesFlyoutItem);

        RebuildAudioTrackFlyout();
        RebuildEmbeddedSubtitleTrackFlyout();
        UpdatePlaybackRateFlyoutChecks();
        UpdateHardwareDecodingFlyoutChecks();
        UpdateAspectRatioFlyoutChecks();
        UpdateDelayFlyoutLabels();
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
            RowSpacing = 12
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerStack = new StackPanel { Spacing = 2 };
        headerStack.Children.Add(new TextBlock
        {
            Text = "Library",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = "Pinned folders, media roots, and quick play actions.",
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        });
        header.Children.Add(headerStack);
        var addButton = new Button
        {
            Padding = new Thickness(12, 8, 12, 8),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE710" },
                    new TextBlock
                    {
                        Text = "Add Root",
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        addButton.Click += AddRootFolder_Click;
        SetControlHint(addButton, "Add library folder");
        Grid.SetColumn(addButton, 1);
        header.Children.Add(addButton);
        var closeButton = CreateDrawerActionButton("\uE711", "Close library", CloseBrowserPane_Click);
        Grid.SetColumn(closeButton, 2);
        header.Children.Add(closeButton);
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
        LibraryTree.ContextRequested += LibraryTree_ContextRequested;
        LibraryTree.RightTapped += LibraryTree_RightTapped;
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
        var grid = new Grid();
        grid.PointerMoved += PlayerPane_PointerMoved;

        var playerStage = new Grid
        {
            Background = new SolidColorBrush(Colors.Black)
        };
        grid.Children.Add(playerStage);

        PlayerHost = new PlaybackHostAdapter(_playbackBackend, _videoPresenter);
        playerStage.Children.Add(PlayerHost.View);

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
        grid.Children.Add(DecoderBadge);

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
        grid.Children.Add(SubtitleOverlayBorder);

        return grid;
    }

    private UIElement BuildPlaylistPane()
    {
        var grid = new Grid
        {
            RowSpacing = 12
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerStack = new StackPanel { Spacing = 2 };
        headerStack.Children.Add(new TextBlock
        {
            Text = "Up Next",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        PlaylistSummaryTextBlock = new TextBlock
        {
            Text = "Queue is empty",
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        };
        headerStack.Children.Add(PlaylistSummaryTextBlock);
        header.Children.Add(headerStack);
        var queueFolderButton = new Button { Content = new FontIcon { Glyph = "\uE8B7" }, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 8, 10, 8) };
        queueFolderButton.Click += QueuePlaylistFolder_Click;
        SetControlHint(queueFolderButton, "Add folder to queue");
        Grid.SetColumn(queueFolderButton, 1);
        header.Children.Add(queueFolderButton);
        var removeButton = new Button { Content = new FontIcon { Glyph = "\uE74D" }, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 8, 10, 8) };
        removeButton.Click += RemoveSelected_Click;
        SetControlHint(removeButton, "Remove selected queued item");
        Grid.SetColumn(removeButton, 2);
        header.Children.Add(removeButton);
        var clearButton = new Button { Content = new FontIcon { Glyph = "\uE894" }, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 8, 10, 8) };
        clearButton.Click += ClearPlaylist_Click;
        SetControlHint(clearButton, "Clear queue");
        Grid.SetColumn(clearButton, 3);
        header.Children.Add(clearButton);
        var closeButton = CreateDrawerActionButton("\uE711", "Close queue", ClosePlaylistPane_Click);
        Grid.SetColumn(closeButton, 4);
        header.Children.Add(closeButton);
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
            RowSpacing = 10
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
            Spacing = 20,
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
            ColumnSpacing = 16
        };
        secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(secondaryRow, 1);

        var secondaryButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        secondaryButtons.Children.Add(CreateSecondaryTransportButton(new FontIcon { Glyph = "\uE896" }, "Back 10 seconds", SeekBack_Click));
        secondaryButtons.Children.Add(CreateSecondaryTransportButton(new FontIcon { Glyph = "\uE100" }, "Previous frame", PreviousFrame_Click));
        secondaryButtons.Children.Add(CreateSecondaryTransportButton(new FontIcon { Glyph = "\uE101" }, "Next frame", NextFrame_Click));
        secondaryButtons.Children.Add(CreateSecondaryTransportButton(new FontIcon { Glyph = "\uE893" }, "Forward 10 seconds", SeekForward_Click));
        var importSubsButton = CreateSecondaryTransportButton(new SymbolIcon(Symbol.Edit), "Import subtitles", ImportSubtitle_Click, "Import Subs");
        secondaryButtons.Children.Add(importSubsButton);
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
            Maximum = 1,
            Value = 0.8,
            Width = 160
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
            Spacing = 10
        };

        LanguageToolsToggleButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        LanguageToolsToggleButton.Click += LanguageToolsToggleButton_Click;
        SetControlHint(LanguageToolsToggleButton, "Toggle language tools");
        drawer.Children.Add(LanguageToolsToggleButton);

        LanguageToolsContentBorder = CreatePanelBorder(
            new SolidColorBrush(ColorHelper.FromArgb(40, 255, 255, 255)),
            new SolidColorBrush(ColorHelper.FromArgb(0, 0, 0, 0)),
            0,
            new CornerRadius(18),
            new Thickness(16, 14, 16, 16));

        var languageGrid = new Grid
        {
            RowSpacing = 10,
            ColumnSpacing = 12
        };
        languageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        languageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        languageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        languageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        languageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var languageHeader = new Grid
        {
            ColumnSpacing = 12
        };
        languageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        languageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var languageHeaderText = new StackPanel { Spacing = 4 };
        languageHeaderText.Children.Add(new TextBlock
        {
            Text = "Language Tools",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        languageHeaderText.Children.Add(new TextBlock
        {
            Text = "Transcription, translation, and subtitle behavior.",
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        });
        languageHeader.Children.Add(languageHeaderText);
        LanguageToolsSubtitleToggleButton = new ToggleButton
        {
            MinWidth = 116,
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        LanguageToolsSubtitleToggleButton.Click += SubtitleVisibilityToggleButton_Click;
        SetControlHint(LanguageToolsSubtitleToggleButton, "Toggle subtitles");
        Grid.SetColumn(LanguageToolsSubtitleToggleButton, 1);
        languageHeader.Children.Add(LanguageToolsSubtitleToggleButton);
        Grid.SetColumnSpan(languageHeader, 2);
        languageGrid.Children.Add(languageHeader);

        var modelRow = new Grid
        {
            ColumnSpacing = 12
        };
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(modelRow, 1);
        Grid.SetColumnSpan(modelRow, 2);

        TranscriptionModelComboBox = new ComboBox { Header = "Transcription Model", MinWidth = 220 };
        foreach (var model in new[]
                 {
                     SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny-multilingual"),
                     SubtitleWorkflowCatalog.GetTranscriptionModel("local:base-multilingual"),
                     SubtitleWorkflowCatalog.GetTranscriptionModel("local:small-multilingual"),
                     SubtitleWorkflowCatalog.GetTranscriptionModel("cloud:gpt-4o-mini-transcribe"),
                     SubtitleWorkflowCatalog.GetTranscriptionModel("cloud:gpt-4o-transcribe"),
                     SubtitleWorkflowCatalog.GetTranscriptionModel("cloud:whisper-1")
                 })
        {
            TranscriptionModelComboBox.Items.Add(model);
        }

        TranscriptionModelComboBox.DisplayMemberPath = nameof(TranscriptionModelSelection.DisplayName);
        TranscriptionModelComboBox.SelectionChanged += TranscriptionModelComboBox_SelectionChanged;
        SetControlHint(TranscriptionModelComboBox, "Transcription model");
        modelRow.Children.Add(TranscriptionModelComboBox);

        TranslationModelComboBox = new ComboBox { Header = "Translation Model", MinWidth = 220 };
        foreach (var model in new[]
                 {
                     SubtitleWorkflowCatalog.GetTranslationModel("local:hymt-1.8b"),
                     SubtitleWorkflowCatalog.GetTranslationModel("local:hymt-7b"),
                     SubtitleWorkflowCatalog.GetTranslationModel("cloud:gpt-5-mini"),
                     SubtitleWorkflowCatalog.GetTranslationModel("cloud:google-translate"),
                     SubtitleWorkflowCatalog.GetTranslationModel("cloud:deepl"),
                     SubtitleWorkflowCatalog.GetTranslationModel("cloud:microsoft-translator")
                 })
        {
            TranslationModelComboBox.Items.Add(model);
        }

        TranslationModelComboBox.DisplayMemberPath = nameof(TranslationModelSelection.DisplayName);
        TranslationModelComboBox.SelectionChanged += TranslationModelComboBox_SelectionChanged;
        TranslationModelComboBox.IsEnabled = false;
        SetControlHint(TranslationModelComboBox, "Translation model");
        Grid.SetColumn(TranslationModelComboBox, 1);
        modelRow.Children.Add(TranslationModelComboBox);
        languageGrid.Children.Add(modelRow);

        var processingRow = new Grid
        {
            ColumnSpacing = 12
        };
        processingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        processingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        processingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(processingRow, 2);
        Grid.SetColumnSpan(processingRow, 2);

        TranslationToggleSwitch = new ToggleSwitch
        {
            Header = "Translate Current Video",
            OffContent = "Off",
            OnContent = "On"
        };
        TranslationToggleSwitch.Toggled += TranslationToggleSwitch_Toggled;
        SetControlHint(TranslationToggleSwitch, "Translate current video");
        processingRow.Children.Add(TranslationToggleSwitch);

        AutoTranslateToggleSwitch = new ToggleSwitch
        {
            Header = "Auto Translate Non-English",
            OffContent = "Off",
            OnContent = "On"
        };
        AutoTranslateToggleSwitch.Toggled += AutoTranslateToggleSwitch_Toggled;
        SetControlHint(AutoTranslateToggleSwitch, "Auto translate non-English");
        Grid.SetColumn(AutoTranslateToggleSwitch, 1);
        processingRow.Children.Add(AutoTranslateToggleSwitch);

        var subtitleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        SubtitleModeComboBox = new ComboBox
        {
            Header = "Subtitle Mode",
            MinWidth = 170
        };
        SubtitleModeComboBox.Items.Add(new ComboBoxItem { Content = "Off", Tag = SubtitleRenderMode.Off });
        SubtitleModeComboBox.Items.Add(new ComboBoxItem { Content = "Source Only", Tag = SubtitleRenderMode.SourceOnly });
        SubtitleModeComboBox.Items.Add(new ComboBoxItem { Content = "Translation Only", Tag = SubtitleRenderMode.TranslationOnly });
        SubtitleModeComboBox.Items.Add(new ComboBoxItem { Content = "Dual", Tag = SubtitleRenderMode.Dual });
        SubtitleModeComboBox.SelectionChanged += SubtitleModeComboBox_SelectionChanged;
        SetControlHint(SubtitleModeComboBox, "Subtitle mode");
        subtitleRow.Children.Add(SubtitleModeComboBox);

        var subtitleStyleButton = new DropDownButton
        {
            Content = "Subtitle Style",
            Flyout = CreateSubtitleStyleFlyout()
        };
        SetControlHint(subtitleStyleButton, "Subtitle style");
        subtitleRow.Children.Add(subtitleStyleButton);
        Grid.SetColumn(subtitleRow, 2);
        processingRow.Children.Add(subtitleRow);
        languageGrid.Children.Add(processingRow);

        LanguageToolsContentBorder.Child = languageGrid;
        drawer.Children.Add(LanguageToolsContentBorder);
        UpdateLanguageToolsDrawerState();
        return drawer;
    }

    private Button CreateSecondaryTransportButton(IconElement icon, string label, RoutedEventHandler handler, string? text = null)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = text is null ? 0 : 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(icon);
        if (!string.IsNullOrWhiteSpace(text))
        {
            content.Children.Add(new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(10, 6, 10, 6),
            MinHeight = 36
        };
        button.Click += handler;
        SetControlHint(button, label);
        return button;
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

    private void InitializeShellState()
    {
        _isInitializingShellState = true;
        var settings = _settingsFacade.Load();
        if (settings.PinnedRoots.Count == 0)
        {
            var defaultVideosPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var pinnedRoots = Directory.Exists(defaultVideosPath) ? new List<string> { defaultVideosPath } : [];
            settings = settings with
            {
                PinnedRoots = pinnedRoots,
                ShowBrowserPanel = pinnedRoots.Count > 0,
                ShowPlaylistPanel = true
            };
        }

        _audioDelaySeconds = settings.AudioDelaySeconds;
        _subtitleDelaySeconds = settings.SubtitleDelaySeconds;
        _selectedAspectRatio = string.IsNullOrWhiteSpace(settings.AspectRatioOverride) ? "auto" : settings.AspectRatioOverride;
        ViewModel.Settings = settings;
        RebuildShortcutBindings();
        ViewModel.WindowTitle = "Babel Player";
        ViewModel.WindowSubtitle = IdleWindowSubtitle;
        ViewModel.StatusMessage = "Open local media or subtitles to start playback.";
        ViewModel.IsStatusOpen = true;
        ViewModel.ActiveHardwareDecoder = "mpv ready";
        ViewModel.Browser.IsVisible = false;
        ViewModel.Queue.IsVisible = false;
        ViewModel.Transport.Volume = Math.Clamp(settings.VolumeLevel, 0, 1);
        ViewModel.Transport.PlaybackRate = settings.DefaultPlaybackRate;
        _lastNonOffSubtitleRenderMode = settings.SubtitleRenderMode == SubtitleRenderMode.Off
            ? SubtitleRenderMode.TranslationOnly
            : settings.SubtitleRenderMode;
        ViewModel.SubtitleOverlay.ShowSource = settings.SubtitleRenderMode is SubtitleRenderMode.SourceOnly or SubtitleRenderMode.Dual;
        ViewModel.SubtitleOverlay.TranslationText = "Drop a file or choose Open to start playback.";
        ViewModel.SelectedTranscriptionLabel = SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny-multilingual").DisplayName;
        ViewModel.SelectedTranslationLabel = SubtitleWorkflowCatalog.GetTranslationModel(null).DisplayName;
        TranslatedSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.TranslationText;
        StatusInfoBar.IsOpen = true;
        StatusInfoBar.Message = ViewModel.StatusMessage;

        ThemeToggleMenuItem.IsChecked = true;
        ApplyTheme(isDark: true);
        VolumeSlider.Value = ViewModel.Transport.Volume;
        MuteToggleButton.IsChecked = settings.IsMuted;
        _ = _shellController.SetVolumeAsync(ViewModel.Transport.Volume);
        _ = _shellController.SetMutedAsync(settings.IsMuted);
        _shellController.SetResumeTrackingEnabled(settings.ResumeEnabled);
        ResumePlaybackToggleItem.IsChecked = settings.ResumeEnabled;
        SetPlaybackRate(settings.DefaultPlaybackRate, persistSettings: false, showStatus: false);
        _windowModeService.EnsureInitialStandardBounds();
        _windowModeService.SetModeAsync(settings.WindowMode).GetAwaiter().GetResult();
        ApplyWindowModeChrome(settings.WindowMode);
        SyncWindowModeButtons(settings.WindowMode);
        UpdateOverlayControlState();
        TranscriptionModelComboBox.SelectedIndex = -1;
        TranslationModelComboBox.SelectedIndex = -1;
        TranslationToggleSwitch.IsOn = false;
        AutoTranslateToggleSwitch.IsOn = false;
        BrowserPaneToggle.IsChecked = ViewModel.Browser.IsVisible;
        PlaylistPaneToggle.IsChecked = ViewModel.Queue.IsVisible;
        ApplySubtitleStyleSettings();
        UpdateSubtitleRenderModeFlyoutChecks();
        UpdateHardwareDecodingFlyoutChecks();
        UpdateAspectRatioFlyoutChecks();
        UpdateDelayFlyoutLabels();
        UpdateMuteButtonVisual();
        UpdatePlayPauseButtonVisual();

        foreach (var root in _libraryBrowserService.BuildPinnedRoots(settings.PinnedRoots))
        {
            ViewModel.Browser.Roots.Add(root);
        }

        RebuildLibraryTree();
        SyncPaneLayout(RootGrid.ActualWidth);
        ApplyAdaptiveStandardLayout(RootGrid.ActualHeight);
        PlayerHost.RequestHostBoundsSync();
        UpdateWindowHeader();
        _isInitializingShellState = false;
    }

    private void UpdateWindowHeader()
    {
        var activeMediaTitle = GetActiveWindowSubtitle();
        ViewModel.WindowTitle = "Babel Player";
        ViewModel.WindowSubtitle = activeMediaTitle ?? IdleWindowSubtitle;
        WindowTitleTextBlock.Text = ViewModel.WindowTitle;
        WindowSubtitleTextBlock.Text = ViewModel.WindowSubtitle;
        Title = activeMediaTitle is null ? ViewModel.WindowTitle : $"{ViewModel.WindowTitle} - {activeMediaTitle}";
    }

    private string? GetActiveWindowSubtitle()
    {
        if (_shellController.NowPlayingItem is { DisplayName: { Length: > 0 } currentItemName })
        {
            return currentItemName;
        }

        if (ViewModel.Queue.SelectedQueueItem is { DisplayName: { Length: > 0 } selectedQueueName })
        {
            return selectedQueueName;
        }

        if (ViewModel.Queue.SelectedHistoryItem is { DisplayName: { Length: > 0 } selectedHistoryName })
        {
            return selectedHistoryName;
        }

        if (_selectedLibraryNode is { IsFolder: false, Name: { Length: > 0 } } selectedLibraryNode)
        {
            return selectedLibraryNode.Name;
        }

        return null;
    }

    private void UpdatePlayPauseButtonVisual()
    {
        if (PlayPauseButton is null)
        {
            return;
        }

        var isPaused = ViewModel.Transport.IsPaused;
        PlayPauseButton.Content = new SymbolIcon(isPaused ? Symbol.Play : Symbol.Pause);
        SetControlHint(PlayPauseButton, isPaused ? "Play" : "Pause");
    }

    private void UpdateMuteButtonVisual()
    {
        if (MuteToggleButton is null)
        {
            return;
        }

        var isMuted = MuteToggleButton.IsChecked == true;
        MuteToggleButton.Content = new SymbolIcon(isMuted ? Symbol.Mute : Symbol.Volume);
        SetControlHint(MuteToggleButton, isMuted ? "Unmute" : "Mute");
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SystemBackdrop = null;
        _micaBackdrop = null;
        _shellController.FlushResumeTracking();
        SaveCurrentSettings();
        _stageCoordinator.Dispose();
        _shellProjectionService.ProjectionChanged -= ShellProjectionService_ProjectionChanged;
        _shellController.QueueSnapshotChanged -= ShellController_QueueSnapshotChanged;
        _shellProjectionService.Dispose();
        (_subtitlePresenter as IDisposable)?.Dispose();
        _subtitleWorkflowController.Dispose();
        _shellController.Dispose();
        _playbackBackendCoordinator.Dispose();
        _ = _playbackBackend.DisposeAsync();
    }

    private void ShellController_QueueSnapshotChanged(PlaybackQueueSnapshot snapshot)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyQueueSnapshot(snapshot);
            return;
        }

        DispatcherQueue.TryEnqueue(() => ApplyQueueSnapshot(snapshot));
    }

    private void SaveCurrentSettings()
    {
        var pinnedRoots = ViewModel.Browser.Roots.Select(root => root.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var updatedSettings = _settingsFacade.UpdatePlaybackDefaults(
            _settingsFacade.UpdateLayout(
                ViewModel.Settings,
                ViewModel.Browser.IsVisible,
                ViewModel.Queue.IsVisible,
                _windowModeService.CurrentMode),
            ViewModel.Settings.HardwareDecodingMode,
            ViewModel.Transport.PlaybackRate,
            _audioDelaySeconds,
            _subtitleDelaySeconds,
            _selectedAspectRatio);

        ViewModel.Settings = updatedSettings with
        {
            PinnedRoots = pinnedRoots,
            ResumeEnabled = ResumePlaybackToggleItem?.IsChecked == true,
            VolumeLevel = Math.Clamp(VolumeSlider?.Value ?? ViewModel.Transport.Volume, 0, 1),
            IsMuted = MuteToggleButton?.IsChecked == true
        };

        _settingsFacade.Save(ViewModel.Settings);
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var files = await _filePickerService.PickMediaFilesAsync();
        await ApplyQueueMutationAsync(_shellController.EnqueueFiles(files, autoplay: true));
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        await QueueFolderIntoPlaylistAsync(autoplay: true);
    }

    private async void AddRootFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await _filePickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        AddPinnedRoot(folder);
    }

    private async void QueuePlaylistFolder_Click(object sender, RoutedEventArgs e)
    {
        await QueueFolderIntoPlaylistAsync(autoplay: false);
    }

    private async void ImportSubtitle_Click(object sender, RoutedEventArgs e)
    {
        var subtitlePath = await _filePickerService.PickSubtitleFileAsync();
        if (string.IsNullOrWhiteSpace(subtitlePath))
        {
            return;
        }

        try
        {
            var result = await _subtitleWorkflowController.ImportExternalSubtitlesAsync(subtitlePath, autoLoaded: false);
            ShowStatus($"Imported {Path.GetFileName(subtitlePath)} with {result.CueCount} cues.");
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, true);
        }
    }

    private void BrowserPaneToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Browser.IsVisible = BrowserPaneToggle.IsChecked == true;
        SyncPaneLayout(RootGrid.ActualWidth);
        SaveCurrentSettings();
    }

    private void PlaylistPaneToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Queue.IsVisible = PlaylistPaneToggle.IsChecked == true;
        SyncPaneLayout(RootGrid.ActualWidth);
        SaveCurrentSettings();
    }

    private void CloseBrowserPane_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Browser.IsVisible = false;
        BrowserPaneToggle.IsChecked = false;
        SyncPaneLayout(RootGrid.ActualWidth);
        SaveCurrentSettings();
    }

    private void ClosePlaylistPane_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Queue.IsVisible = false;
        PlaylistPaneToggle.IsChecked = false;
        SyncPaneLayout(RootGrid.ActualWidth);
        SaveCurrentSettings();
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

    private void SubtitleVisibilityToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleSubtitleVisibility();
    }

    private void OverlaySubtitleToggleButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        ToggleSubtitleVisibility();
    }

    private void ToggleSubtitleVisibility()
    {
        var currentMode = GetEffectiveSubtitleRenderMode();
        var subtitlesEnabled = currentMode != SubtitleRenderMode.Off;
        if (subtitlesEnabled)
        {
            _lastNonOffSubtitleRenderMode = currentMode;
        }

        var nextMode = subtitlesEnabled ? SubtitleRenderMode.Off : _lastNonOffSubtitleRenderMode;
        ViewModel.Settings = ViewModel.Settings with
        {
            SubtitleRenderMode = nextMode
        };
        if (nextMode != SubtitleRenderMode.Off)
        {
            _lastNonOffSubtitleRenderMode = nextMode;
        }

        UpdateSubtitleVisibility();
        UpdateSubtitleRenderModeFlyoutChecks();
        UpdateOverlayControlState();
        SaveCurrentSettings();
        ShowStatus(subtitlesEnabled ? "Subtitles hidden." : "Subtitles shown.");
    }

    private static string NormalizeWinUiTranscriptionModelKey(string? modelKey)
    {
        return modelKey switch
        {
            "local:tiny" => "local:tiny-multilingual",
            "local:base" => "local:base-multilingual",
            "local:small" => "local:small-multilingual",
            _ => string.IsNullOrWhiteSpace(modelKey)
                ? "local:tiny-multilingual"
                : modelKey
        };
    }

    private async Task NormalizeWinUiTranscriptionSelectionAsync(string normalizedKey)
    {
        if (_isNormalizingWinUITranscriptionSelection)
        {
            return;
        }

        _isNormalizingWinUITranscriptionSelection = true;
        try
        {
            await _subtitleWorkflowController.SelectTranscriptionModelAsync(normalizedKey, suppressStatus: true);
        }
        finally
        {
            _isNormalizingWinUITranscriptionSelection = false;
        }
    }

    private bool HasSourceOnlyOverrideForCurrentVideo(SubtitleWorkflowSnapshot? snapshot = null)
    {
        if (_subtitleWorkflowController is null)
        {
            return false;
        }

        snapshot ??= _subtitleWorkflowController.Snapshot;
        return !string.IsNullOrWhiteSpace(_subtitleSourceOnlyOverrideVideoPath)
            && !string.IsNullOrWhiteSpace(snapshot.CurrentVideoPath)
            && string.Equals(_subtitleSourceOnlyOverrideVideoPath, snapshot.CurrentVideoPath, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearSourceOnlyOverrideIfInactive(SubtitleWorkflowSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(_subtitleSourceOnlyOverrideVideoPath))
        {
            return;
        }

        if (!snapshot.IsTranslationEnabled
            || string.IsNullOrWhiteSpace(snapshot.CurrentVideoPath)
            || !string.Equals(_subtitleSourceOnlyOverrideVideoPath, snapshot.CurrentVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            _subtitleSourceOnlyOverrideVideoPath = null;
        }
    }

    private SubtitleRenderMode GetEffectiveSubtitleRenderMode(SubtitleWorkflowSnapshot? snapshot = null)
    {
        if (_subtitleWorkflowController is null)
        {
            return ViewModel.Settings.SubtitleRenderMode;
        }

        return _subtitleWorkflowController.GetEffectiveRenderMode(
            ViewModel.Settings.SubtitleRenderMode,
            HasSourceOnlyOverrideForCurrentVideo(snapshot));
    }

    private async void PlaylistList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItem item)
        {
            await ApplyQueueMutationAsync(_shellController.PlayNow(item.Path));
        }
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Queue.SelectedQueueItem = PlaylistList.SelectedItem as PlaylistItem;
        UpdateWindowHeader();
    }

    private async void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItem item)
        {
            await ApplyQueueMutationAsync(_shellController.PlayNow(item.Path));
        }
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Queue.SelectedHistoryItem = HistoryList.SelectedItem as PlaylistItem;
        UpdateWindowHeader();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedIndex < 0)
        {
            return;
        }

        _shellController.RemoveQueueItemAt(PlaylistList.SelectedIndex);
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _shellController.ClearQueue();
        ShowStatus("Queue cleared.");
    }

    private async void PreviousTrack_Click(object sender, RoutedEventArgs e)
    {
        await LoadPlaybackItemAsync(_shellController.MovePrevious());
    }

    private async void NextTrack_Click(object sender, RoutedEventArgs e)
    {
        await LoadPlaybackItemAsync(_shellController.MoveNext());
    }

    private void SeekBack_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        _ = _shellController.SeekRelativeAsync(TimeSpan.FromSeconds(-10));
    }

    private void SeekForward_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        _ = _shellController.SeekRelativeAsync(TimeSpan.FromSeconds(10));
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        if (ViewModel.Transport.IsPaused)
        {
            _ = _shellController.PlayAsync();
            ShowStatus("Playback resumed.");
            return;
        }

        _ = _shellController.PauseAsync();
        ShowStatus("Playback paused.");
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        _ = _shellController.StepFrameAsync(forward: false);
        ViewModel.Transport.IsPaused = true;
        ShowStatus("Stepped to previous frame.");
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        _ = _shellController.StepFrameAsync(forward: true);
        ViewModel.Transport.IsPaused = true;
        ShowStatus("Stepped to next frame.");
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressPositionSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.5)
        {
            return;
        }

        _ = _shellController.SeekAsync(TimeSpan.FromSeconds(e.NewValue));
        if (_isPositionScrubbing)
        {
            UpdateScrubTimeLabels(e.NewValue, PositionSlider.Maximum);
        }
    }

    private void FullscreenPositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (FullscreenPositionSlider is null || _suppressFullscreenSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.5)
        {
            return;
        }

        RegisterFullscreenOverlayInteraction();
        _ = _shellController.SeekAsync(TimeSpan.FromSeconds(e.NewValue));
        UpdateScrubTimeLabels(e.NewValue, FullscreenPositionSlider.Maximum);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ViewModel.Transport.Volume = e.NewValue;
        _ = _shellController.SetVolumeAsync(e.NewValue);
        if (!_isInitializingShellState)
        {
            SaveCurrentSettings();
        }
    }

    private void MuteToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _shellController.SetMutedAsync(MuteToggleButton.IsChecked == true);
        UpdateMuteButtonVisual();
        if (!_isInitializingShellState)
        {
            SaveCurrentSettings();
        }
    }

    private void SubtitleModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkflowControlEvents)
        {
            return;
        }

        if (SubtitleModeComboBox.SelectedItem is not ComboBoxItem { Tag: SubtitleRenderMode mode })
        {
            return;
        }

        ApplySubtitleRenderMode(mode);
    }

    private void PlaybackRateFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: double rate })
        {
            return;
        }

        SetPlaybackRate(rate);
    }

    private void SetPlaybackRate(double speed, bool persistSettings = true, bool showStatus = true)
    {
        var clamped = Math.Clamp(speed, 0.25, 2.0);
        ViewModel.Transport.PlaybackRate = clamped;
        _ = _shellController.SetPlaybackRateAsync(clamped);
        UpdatePlaybackRateFlyoutChecks();
        if (persistSettings && !_isInitializingShellState)
        {
            SaveCurrentSettings();
        }

        if (showStatus)
        {
            ShowStatus($"Playback speed: {clamped:0.00}x");
        }
    }

    private async void TranscriptionModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkflowControlEvents)
        {
            return;
        }

        if (TranscriptionModelComboBox.SelectedItem is not TranscriptionModelSelection selection)
        {
            return;
        }

        await PrepareForTranscriptionRefreshAsync();
        var applied = await _subtitleWorkflowController.SelectTranscriptionModelAsync(selection.Key);
        if (!applied)
        {
            ApplyWorkflowSnapshot(_subtitleWorkflowController.Snapshot);
        }
    }

    private async void TranslationModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkflowControlEvents)
        {
            return;
        }

        if (TranslationModelComboBox.SelectedItem is not TranslationModelSelection selection)
        {
            return;
        }

        var applied = await _subtitleWorkflowController.SelectTranslationModelAsync(selection.Key);
        if (!applied)
        {
            ApplyWorkflowSnapshot(_subtitleWorkflowController.Snapshot);
        }
    }

    private async void TranslationToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || TranslationToggleSwitch is null)
        {
            return;
        }

        await _subtitleWorkflowController.SetTranslationEnabledAsync(TranslationToggleSwitch.IsOn);
    }

    private async void AutoTranslateToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || AutoTranslateToggleSwitch is null)
        {
            return;
        }

        await _subtitleWorkflowController.SetAutoTranslateEnabledAsync(AutoTranslateToggleSwitch.IsOn);
    }

    private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (ShouldIgnoreShortcutInput())
        {
            return;
        }

        if (TryHandleShortcut(new ShortcutKeyInput(e.Key, IsCtrlPressed(), IsAltPressed(), IsShiftPressed())))
        {
            e.Handled = true;
        }
    }

    private bool PlayerHost_ShortcutKeyPressed(ShortcutKeyInput input) => TryHandleShortcut(input);

    private bool TryHandleShortcut(ShortcutKeyInput input)
    {
        if (!TryGetShortcutCommand(input, out var commandId))
        {
            return false;
        }

        _ = ExecuteShortcutCommandAsync(commandId);
        return true;
    }

    private async Task ExecuteShortcutCommandAsync(string commandId)
    {
        switch (commandId)
        {
            case "play_pause":
                PlayPauseButton_Click(this, new RoutedEventArgs());
                break;
            case "seek_back_small":
                _ = _shellController.SeekRelativeAsync(TimeSpan.FromSeconds(-5));
                RegisterFullscreenOverlayInteraction();
                break;
            case "seek_forward_small":
                _ = _shellController.SeekRelativeAsync(TimeSpan.FromSeconds(5));
                RegisterFullscreenOverlayInteraction();
                break;
            case "seek_back_large":
                _ = _shellController.SeekRelativeAsync(TimeSpan.FromSeconds(-15));
                RegisterFullscreenOverlayInteraction();
                break;
            case "seek_forward_large":
                _ = _shellController.SeekRelativeAsync(TimeSpan.FromSeconds(15));
                RegisterFullscreenOverlayInteraction();
                break;
            case "previous_frame":
                _ = _shellController.StepFrameAsync(forward: false);
                RegisterFullscreenOverlayInteraction();
                break;
            case "next_frame":
                _ = _shellController.StepFrameAsync(forward: true);
                RegisterFullscreenOverlayInteraction();
                break;
            case "fullscreen":
                if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
                {
                    await ExitFullscreenAsync();
                }
                else
                {
                    await EnterFullscreenAsync();
                }

                break;
            case "pip":
                await SetWindowModeAsync(_windowModeService.CurrentMode == PlaybackWindowMode.PictureInPicture
                    ? PlaybackWindowMode.Standard
                    : PlaybackWindowMode.PictureInPicture);
                break;
            case "mute":
                MuteToggleButton.IsChecked = !(MuteToggleButton.IsChecked == true);
                MuteToggleButton_Click(MuteToggleButton, new RoutedEventArgs());
                break;
            case "subtitle_delay_back":
                AdjustSubtitleDelay(-0.05);
                break;
            case "subtitle_delay_forward":
                AdjustSubtitleDelay(0.05);
                break;
            case "audio_delay_back":
                AdjustAudioDelay(-0.05);
                break;
            case "audio_delay_forward":
                AdjustAudioDelay(0.05);
                break;
            case "speed_up":
                SetPlaybackRateShortcut(ViewModel.Transport.PlaybackRate + 0.25);
                break;
            case "speed_down":
                SetPlaybackRateShortcut(ViewModel.Transport.PlaybackRate - 0.25);
                break;
            case "speed_reset":
                SetPlaybackRateShortcut(1.0);
                break;
            case "next_item":
                await LoadPlaybackItemAsync(_shellController.MoveNext());
                break;
            case "previous_item":
                await LoadPlaybackItemAsync(_shellController.MovePrevious());
                break;
            case "subtitle_toggle":
                ToggleSubtitleVisibility();
                break;
            case "translation_toggle":
                TranslationToggleSwitch.IsOn = !TranslationToggleSwitch.IsOn;
                break;
        }
    }

    private void SetPlaybackRateShortcut(double speed)
    {
        SetPlaybackRate(speed);
    }

    private bool TryGetShortcutCommand(ShortcutKeyInput input, out string commandId)
    {
        foreach (var binding in _resolvedShortcutBindings.Values)
        {
            if (!binding.Matches(input))
            {
                continue;
            }

            commandId = binding.CommandId;
            return true;
        }

        commandId = string.Empty;
        return false;
    }

    private void RebuildShortcutBindings()
    {
        _resolvedShortcutBindings.Clear();
        foreach (var binding in ViewModel.Settings.ShortcutProfile.Bindings)
        {
            if (!TryResolveShortcutBinding(binding.Key, binding.Value, out var resolved))
            {
                continue;
            }

            _resolvedShortcutBindings[binding.Key] = resolved;
        }
    }

    private bool TryResolveShortcutBinding(string commandId, string gestureText, out ResolvedShortcutBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(gestureText))
        {
            return false;
        }

        ShortcutGesture parsed;
        try
        {
            parsed = _shortcutService.ParseGesture(gestureText);
        }
        catch (FormatException)
        {
            return false;
        }

        var ctrl = false;
        var alt = false;
        var shift = false;
        foreach (var modifier in parsed.Modifiers)
        {
            if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || modifier.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
            }
            else if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase) || modifier.Equals("Menu", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
            }
            else if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
            }
            else
            {
                return false;
            }
        }

        if (!TryParseShortcutKey(parsed.Key, out var key))
        {
            return false;
        }

        binding = new ResolvedShortcutBinding(commandId, key, ctrl, alt, shift);
        return true;
    }

    private static bool TryParseShortcutKey(string keyToken, out VirtualKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(keyToken))
        {
            return false;
        }

        switch (keyToken.Trim())
        {
            case "Space":
                key = VirtualKey.Space;
                return true;
            case "Left":
                key = VirtualKey.Left;
                return true;
            case "Right":
                key = VirtualKey.Right;
                return true;
            case "PageUp":
                key = VirtualKey.PageUp;
                return true;
            case "PageDown":
                key = VirtualKey.PageDown;
                return true;
            case "F11":
                key = VirtualKey.F11;
                return true;
            case "OemMinus":
                key = (VirtualKey)0xBD;
                return true;
            case "OemPlus":
                key = (VirtualKey)0xBB;
                return true;
            case "OemComma":
                key = (VirtualKey)0xBC;
                return true;
            case "OemPeriod":
                key = (VirtualKey)0xBE;
                return true;
            case "D0":
                key = (VirtualKey)0x30;
                return true;
        }

        if (keyToken.Length == 1)
        {
            var upper = char.ToUpperInvariant(keyToken[0]);
            if (upper is >= 'A' and <= 'Z')
            {
                key = (VirtualKey)upper;
                return true;
            }
        }

        return Enum.TryParse(keyToken, true, out key);
    }

    private bool ShouldIgnoreShortcutInput()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        return focused is TextBox or PasswordBox or RichEditBox or ComboBox;
    }

    private static bool IsCtrlPressed() => IsVirtualKeyPressed(VirtualKey.Control);

    private static bool IsAltPressed() => IsVirtualKeyPressed(VirtualKey.Menu);

    private static bool IsShiftPressed() => IsVirtualKeyPressed(VirtualKey.Shift);

    private static bool IsVirtualKeyPressed(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void ThemeToggleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(ThemeToggleMenuItem.IsChecked);
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
            ColumnSpacing = 16
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel
        {
            Spacing = 2
        };
        titleStack.Children.Add(new TextBlock
        {
            Text = "Language Tools",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = _autoCollapsedLanguageToolsForPortraitVideo
                ? _isLanguageToolsAutoCollapseOverridden
                    ? "Reopened manually for portrait video."
                    : "Collapsed to preserve height for portrait video. Click Show to reopen."
                : "Transcription, translation, and subtitle behavior.",
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        });
        header.Children.Add(titleStack);
        var actionText = new TextBlock
        {
            Text = isEffectivelyExpanded ? "Hide" : "Show",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 209, 219, 233))
        };
        Grid.SetColumn(actionText, 1);
        header.Children.Add(actionText);
        LanguageToolsToggleButton.Content = header;
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
            var playbackSnapshot = _shellController.CurrentPlaybackSnapshot;
            var displayWidth = playbackSnapshot.VideoDisplayWidth > 0
                ? playbackSnapshot.VideoDisplayWidth
                : playbackSnapshot.VideoWidth;
            var displayHeight = playbackSnapshot.VideoDisplayHeight > 0
                ? playbackSnapshot.VideoDisplayHeight
                : playbackSnapshot.VideoHeight;

            if (displayWidth > 0
                && displayHeight > 0
                && displayHeight > displayWidth
                && PlayerHost.View.ActualWidth > 0
                && PlayerHost.View.ActualHeight > 0)
            {
                var requiredHeightAtCurrentWidth = PlayerHost.View.ActualWidth * displayHeight / (double)displayWidth;
                shouldAutoCollapse = requiredHeightAtCurrentWidth > PlayerHost.View.ActualHeight + 8;
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

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (await TryHandleLibraryQueueDropAsync(sender, e.DataView))
        {
            e.Handled = true;
            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        var storageItems = await e.DataView.GetStorageItemsAsync();
        List<string> files = [];
        List<string> folders = [];
        foreach (var item in storageItems)
        {
            switch (item)
            {
                case StorageFile file when LibraryBrowserService.IsSupportedMediaFile(file.Path):
                    files.Add(file.Path);
                    break;
                case StorageFolder folder:
                    folders.Add(folder.Path);
                    break;
            }
        }

        if (IsPlaylistDropTarget(sender))
        {
            var result = _shellController.AddToQueue(
                files.Concat(folders.SelectMany(folder => _libraryBrowserService.EnumerateMediaFiles(folder, recursive: true))));
            if (!string.IsNullOrWhiteSpace(result.StatusMessage))
            {
                ShowStatus(result.StatusMessage, result.IsError);
            }

            return;
        }

        await ApplyQueueMutationAsync(_shellController.EnqueueDroppedItems(files, folders));
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(LibraryQueueDragFormat))
        {
            e.AcceptedOperation = IsPlaylistDropTarget(sender)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BrowserColumn is null
            || PlaylistColumn is null
            || BrowserPane is null
            || PlaylistPane is null
            || PlayerHost is null
            || SourceSubtitleTextBlock is null
            || TranslatedSubtitleTextBlock is null
            || SubtitleOverlayBorder is null)
        {
            return;
        }

        SyncPaneLayout(e.NewSize.Width);
        ApplyAdaptiveStandardLayout(e.NewSize.Height);
        UpdatePortraitVideoLanguageToolsState();
        if (!string.IsNullOrWhiteSpace(_pendingAutoFitPath))
        {
            TryApplyStandardAutoFit();
        }

        PlayerHost.RequestHostBoundsSync();
        UpdateSubtitleVisibility();
        _stageCoordinator.HandleStageLayoutChanged();
    }

    private void LibraryTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not LibraryNode node || !node.IsFolder)
        {
            return;
        }

        if (args.Node.Children.Count > 0 && !args.Node.HasUnrealizedChildren)
        {
            return;
        }

        args.Node.Children.Clear();
        foreach (var child in _libraryBrowserService.BuildRootNode(node.Path).Children)
        {
            args.Node.Children.Add(CreateTreeNode(child));
        }

        args.Node.HasUnrealizedChildren = false;
    }

    private async void LibraryTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not LibraryNode node)
        {
            return;
        }

        _selectedLibraryNode = node;
        if (node.IsFolder)
        {
            if (sender.SelectedNode is not null)
            {
                sender.SelectedNode.IsExpanded = !sender.SelectedNode.IsExpanded;
                node.IsExpanded = sender.SelectedNode.IsExpanded;
            }

            UpdateWindowHeader();
            return;
        }

        await LoadLibraryNodeAsync(node);
    }

    private async void LibraryTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is not LibraryNode node)
        {
            return;
        }

        _selectedLibraryNode = node;
        UpdateWindowHeader();
        if (node.IsFolder)
        {
            return;
        }

        await Task.Yield();
        if (_isLibraryDragOperationInProgress)
        {
            return;
        }

        await LoadLibraryNodeAsync(node);
    }

    private void LibraryTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        var paths = args.Items
            .OfType<LibraryNode>()
            .Where(node => !node.IsFolder && !string.IsNullOrWhiteSpace(node.Path))
            .Select(node => node.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            args.Cancel = true;
            return;
        }

        _isLibraryDragOperationInProgress = true;
        args.Data.RequestedOperation = DataPackageOperation.Copy;
        args.Data.SetData(LibraryQueueDragFormat, string.Join('\n', paths));
    }

    private void LibraryTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        _isLibraryDragOperationInProgress = false;
    }

    private async Task LoadLibraryNodeAsync(LibraryNode node)
    {
        if (node.IsFolder || string.IsNullOrWhiteSpace(node.Path))
        {
            return;
        }

        if (string.Equals(_pendingLibraryLoadPath, node.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingLibraryLoadPath = node.Path;
        try
        {
            await ApplyQueueMutationAsync(_shellController.PlayNow(node.Path));
        }
        finally
        {
            _pendingLibraryLoadPath = null;
        }
    }

    private void LibraryTree_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (!TryGetLibraryNodeFromElement(e.OriginalSource as DependencyObject, out var anchor, out var node))
        {
            return;
        }

        ShowLibraryContextMenu(anchor, node);
        e.Handled = true;
    }

    private void LibraryTree_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (!TryGetLibraryNodeFromElement(args.OriginalSource as DependencyObject, out var anchor, out var node))
        {
            return;
        }

        ShowLibraryContextMenu(anchor, node, args);
        args.Handled = true;
    }

    private void ShowLibraryContextMenu(
        FrameworkElement anchor,
        LibraryNode node,
        Microsoft.UI.Xaml.Input.ContextRequestedEventArgs? contextArgs = null)
    {
        _selectedLibraryNode = node;
        UpdateWindowHeader();
        var menu = new MenuFlyout();
        if (node.IsFolder)
        {
            var queueFolderItem = new MenuFlyoutItem { Text = "Queue Folder" };
            queueFolderItem.Click += async (_, _) => await ApplyQueueMutationAsync(_shellController.EnqueueFolder(node.Path, autoplay: false));
            menu.Items.Add(queueFolderItem);

            var isPinnedRoot = ViewModel.Browser.Roots.Any(root => string.Equals(root.Path, node.Path, StringComparison.OrdinalIgnoreCase));
            var pinItem = new MenuFlyoutItem
            {
                Text = isPinnedRoot ? "Unpin Root" : "Pin Root"
            };
            pinItem.Click += (_, _) =>
            {
                if (isPinnedRoot)
                {
                    RemovePinnedRoot(node.Path);
                }
                else
                {
                    AddPinnedRoot(node.Path);
                }
            };
            menu.Items.Add(pinItem);
        }
        else
        {
            var playNowItem = new MenuFlyoutItem { Text = "Play Now" };
            playNowItem.Click += async (_, _) => await ApplyQueueMutationAsync(_shellController.PlayNow(node.Path));
            menu.Items.Add(playNowItem);

            var playNextItem = new MenuFlyoutItem { Text = "Play Next" };
            playNextItem.Click += (_, _) =>
            {
                var result = _shellController.PlayNext(node.Path);
                if (!string.IsNullOrWhiteSpace(result.StatusMessage))
                {
                    ShowStatus(result.StatusMessage, result.IsError);
                }
            };
            menu.Items.Add(playNextItem);

            var addItem = new MenuFlyoutItem { Text = "Add to Queue" };
            addItem.Click += (_, _) =>
            {
                var result = _shellController.AddToQueue([node.Path]);
                ShowStatus(result.StatusMessage ?? "Unable to add media to the queue.", result.IsError);
            };
            menu.Items.Add(addItem);
        }

        var openExplorerItem = new MenuFlyoutItem { Text = "Open in Explorer" };
        openExplorerItem.Click += (_, _) => OpenInExplorer(node.Path, selectItem: !node.IsFolder);
        menu.Items.Add(openExplorerItem);
        if (contextArgs is not null && contextArgs.TryGetPosition(anchor, out var point))
        {
            menu.ShowAt(anchor, new FlyoutShowOptions
            {
                Position = point
            });
            return;
        }

        menu.ShowAt(anchor);
    }

    private void PlayerHost_MediaOpened(PlaybackStateSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            PlayerHost.RequestHostBoundsSync();
            UpdatePortraitVideoLanguageToolsState();
            TryApplyStandardAutoFit();
            await _shellController.SetAudioDelayAsync(_audioDelaySeconds);
            await _shellController.SetSubtitleDelayAsync(_subtitleDelaySeconds);
            await _shellController.SetAspectRatioAsync(_selectedAspectRatio);
            UpdateWindowHeader();
            var result = await _shellController.HandleMediaOpenedAsync(
                snapshot,
                ViewModel.Settings.ResumeEnabled);
            if (result.ResumePosition is TimeSpan resumePosition)
            {
                var duration = snapshot.Duration > TimeSpan.Zero
                    ? snapshot.Duration
                    : _shellController.CurrentPlaybackSnapshot.Duration;
                var path = !string.IsNullOrWhiteSpace(snapshot.Path)
                    ? snapshot.Path
                    : _shellController.CurrentPlaybackSnapshot.Path;
                UpdatePositionSurfaces(resumePosition, duration);
                ShowStatus($"Resumed: {Path.GetFileName(path)}");
                return;
            }

            ShowStatus(result.StatusMessage);
        });
    }

    private void PlayerHost_PlaybackStateChanged(PlaybackStateSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (snapshot.VideoWidth > 0 && snapshot.VideoHeight > 0)
            {
                UpdatePortraitVideoLanguageToolsState();
                TryApplyStandardAutoFit();
            }
        });
    }

    private void PlayerHost_MediaEnded(PlaybackStateSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var result = _shellController.HandleMediaEnded(ViewModel.Settings.ResumeEnabled);
            if (result.NextItem is null)
            {
                ShowStatus(result.StatusMessage);
                return;
            }

            await LoadPlaybackItemAsync(result.NextItem);
        });
    }

    private void PlayerHost_MediaFailed(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowStatus(message, true);
        });
    }

    private void PlayerHost_TracksChanged(IReadOnlyList<MediaTrackInfo> tracks)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _currentTracks.Clear();
            _currentTracks.AddRange(tracks);
            RebuildAudioTrackFlyout();
            RebuildEmbeddedSubtitleTrackFlyout();
            var audioTracks = tracks.Count(track => track.Kind == MediaTrackKind.Audio);
            var subtitleTracks = tracks.Count(track => track.Kind == MediaTrackKind.Subtitle);
            ShowStatus($"Tracks updated. Audio: {audioTracks}, subtitles: {subtitleTracks}.");
        });
    }

    private void PlayerHost_RuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        DispatcherQueue.TryEnqueue(() => ShowStatus($"Runtime setup: {progress.Stage}."));
    }

    private void PlayerHost_InputActivity()
    {
        void showOverlay()
        {
            ShowFullscreenOverlay();
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            showOverlay();
            return;
        }

        DispatcherQueue.TryEnqueue(showOverlay);
    }

    private void PlayerHost_FullscreenExitRequested()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
            {
                await ExitFullscreenAsync();
            }
        });
    }

    private void SubtitleWorkflowController_StatusChanged(string message)
    {
        DispatcherQueue.TryEnqueue(() => ShowStatus(message));
    }

    private void SubtitleWorkflowController_SnapshotChanged(SubtitleWorkflowSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            ApplyWorkflowSnapshot(snapshot);
            await ApplyCaptionStartupGateAsync(snapshot);
        });
    }

    private void ShellProjectionService_ProjectionChanged(ShellProjectionSnapshot projection)
    {
        DispatcherQueue.TryEnqueue(() => ApplyShellProjection(projection));
    }

    private void ApplyShellProjection(ShellProjectionSnapshot projection)
    {
        ViewModel.Transport.PositionSeconds = projection.Transport.PositionSeconds;
        ViewModel.Transport.DurationSeconds = projection.Transport.DurationSeconds;
        ViewModel.Transport.CurrentTimeText = projection.Transport.CurrentTimeText;
        ViewModel.Transport.DurationText = projection.Transport.DurationText;
        ViewModel.Transport.IsPaused = projection.Transport.IsPaused;
        ViewModel.Transport.IsMuted = projection.Transport.IsMuted;
        ViewModel.Transport.Volume = projection.Transport.Volume;
        ViewModel.Transport.PlaybackRate = projection.Transport.PlaybackRate;
        ViewModel.ActiveHardwareDecoder = projection.Transport.ActiveHardwareDecoder;
        HardwareDecoderTextBlock.Text = ViewModel.ActiveHardwareDecoder;

        ViewModel.SubtitleOverlay.SourceText = projection.Subtitle.SourceText;
        ViewModel.SubtitleOverlay.TranslationText = projection.Subtitle.TranslationText;
        ViewModel.SubtitleOverlay.StatusText = projection.Subtitle.StatusText;
        ViewModel.SubtitleOverlay.SubtitleSource = projection.Subtitle.Source;
        ViewModel.SubtitleOverlay.IsCaptionGenerationInProgress = projection.Subtitle.IsCaptionGenerationInProgress;
        ViewModel.SubtitleOverlay.IsTranslationEnabled = projection.Subtitle.IsTranslationEnabled;
        ViewModel.SubtitleOverlay.IsAutoTranslateEnabled = projection.Subtitle.IsAutoTranslateEnabled;

        UpdatePositionSurfaces(
            TimeSpan.FromSeconds(projection.Transport.PositionSeconds),
            TimeSpan.FromSeconds(projection.Transport.DurationSeconds));

        UpdateTrackProjection(projection.SelectedTracks.Tracks);
        UpdateSubtitleVisibility();
        UpdatePlayPauseButtonVisual();
        UpdateOverlayControlState();
        CurrentTimeTextBlock.Text = ViewModel.Transport.CurrentTimeText;
        DurationTextBlock.Text = ViewModel.Transport.DurationText;
        MuteToggleButton.IsChecked = ViewModel.Transport.IsMuted;
        UpdateMuteButtonVisual();
    }

    private void UpdateTrackProjection(IReadOnlyList<MediaTrackInfo> tracks)
    {
        if (HasEquivalentTrackProjection(_currentTracks, tracks))
        {
            return;
        }

        _currentTracks.Clear();
        _currentTracks.AddRange(tracks.Select(track => new MediaTrackInfo
        {
            Id = track.Id,
            FfIndex = track.FfIndex,
            Kind = track.Kind,
            Title = track.Title,
            Language = track.Language,
            Codec = track.Codec,
            IsEmbedded = track.IsEmbedded,
            IsSelected = track.IsSelected,
            IsTextBased = track.IsTextBased
        }));
        RebuildAudioTrackFlyout();
        RebuildEmbeddedSubtitleTrackFlyout();
    }

    private static bool HasEquivalentTrackProjection(IReadOnlyList<MediaTrackInfo> current, IReadOnlyList<MediaTrackInfo> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = next[index];
            if (left.Id != right.Id
                || left.Kind != right.Kind
                || left.IsSelected != right.IsSelected
                || !string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                || !string.Equals(left.Language, right.Language, StringComparison.Ordinal)
                || !string.Equals(left.Codec, right.Codec, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyWorkflowSnapshot(SubtitleWorkflowSnapshot snapshot)
    {
        _suppressWorkflowControlEvents = true;
        try
        {
            ClearSourceOnlyOverrideIfInactive(snapshot);
            var normalizedTranscriptionKey = NormalizeWinUiTranscriptionModelKey(snapshot.SelectedTranscriptionModelKey);
            var normalizedTranscriptionSelection = SubtitleWorkflowCatalog.GetTranscriptionModel(normalizedTranscriptionKey);
            if (!_isNormalizingWinUITranscriptionSelection
                && !string.Equals(normalizedTranscriptionKey, snapshot.SelectedTranscriptionModelKey, StringComparison.Ordinal))
            {
                _ = NormalizeWinUiTranscriptionSelectionAsync(normalizedTranscriptionKey);
            }

            ViewModel.SelectedTranscriptionLabel = normalizedTranscriptionSelection.DisplayName;
            ViewModel.SelectedTranslationLabel = snapshot.SelectedTranslationLabel;
            ViewModel.IsTranslationEnabled = snapshot.IsTranslationEnabled;
            ViewModel.IsAutoTranslateEnabled = snapshot.AutoTranslateEnabled;
            ViewModel.SubtitleSource = snapshot.SubtitleSource;
            ViewModel.IsCaptionGenerationInProgress = snapshot.IsCaptionGenerationInProgress;
            ViewModel.SubtitleOverlay.SelectedTranscriptionLabel = normalizedTranscriptionSelection.DisplayName;
            ViewModel.SubtitleOverlay.SelectedTranslationLabel = snapshot.SelectedTranslationLabel;

            if (TranscriptionModelComboBox is not null)
            {
                TranscriptionModelComboBox.SelectedItem = TranscriptionModelComboBox.Items
                    .OfType<TranscriptionModelSelection>()
                    .FirstOrDefault(item => string.Equals(item.Key, normalizedTranscriptionKey, StringComparison.Ordinal));
            }

            if (TranslationModelComboBox is not null)
            {
                TranslationModelComboBox.SelectedItem = TranslationModelComboBox.Items
                    .OfType<TranslationModelSelection>()
                    .FirstOrDefault(item => string.Equals(item.Key, snapshot.SelectedTranslationModelKey, StringComparison.Ordinal));
                TranslationModelComboBox.IsEnabled = snapshot.IsTranslationEnabled;
                TranslationModelComboBox.Opacity = snapshot.IsTranslationEnabled ? 1 : 0.55;
            }

            if (TranslationToggleSwitch is not null)
            {
                TranslationToggleSwitch.IsOn = snapshot.IsTranslationEnabled;
            }

            if (AutoTranslateToggleSwitch is not null)
            {
                AutoTranslateToggleSwitch.IsOn = snapshot.AutoTranslateEnabled;
            }

            if (ExportCurrentSubtitlesFlyoutItem is not null)
            {
                ExportCurrentSubtitlesFlyoutItem.IsEnabled = snapshot.Cues.Count > 0;
            }

            UpdateSubtitleRenderModeFlyoutChecks(snapshot);
        }
        finally
        {
            _suppressWorkflowControlEvents = false;
        }

        UpdateSubtitleOverlay(snapshot);
    }

    private void ApplyTheme(bool isDark)
    {
        ViewModel.IsDarkTheme = isDark;
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

    private void UpdateAspectRatioFlyoutChecks()
    {
        if (AspectRatioFlyoutSubItem is null)
        {
            return;
        }

        foreach (var item in AspectRatioFlyoutSubItem.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = string.Equals(item.Tag as string, _selectedAspectRatio, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UpdateSubtitleRenderModeFlyoutChecks(SubtitleWorkflowSnapshot? snapshot = null)
    {
        var checkedMode = GetEffectiveSubtitleRenderMode(snapshot);
        if (SubtitleModeComboBox is not null)
        {
            foreach (var item in SubtitleModeComboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is SubtitleRenderMode mode && mode == checkedMode)
                {
                    _suppressWorkflowControlEvents = true;
                    SubtitleModeComboBox.SelectedItem = item;
                    _suppressWorkflowControlEvents = false;
                    break;
                }
            }
        }
    }

    private void UpdatePlaybackRateFlyoutChecks()
    {
        if (PlaybackRateFlyoutSubItem is null)
        {
            return;
        }

        foreach (var item in PlaybackRateFlyoutSubItem.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = item.Tag is double rate && Math.Abs(rate - ViewModel.Transport.PlaybackRate) < 0.001;
        }

        PlaybackRateFlyoutSubItem.Text = $"Playback Rate ({ViewModel.Transport.PlaybackRate:0.00}x)";
    }

    private void UpdateHardwareDecodingFlyoutChecks()
    {
        if (HardwareDecodingFlyoutSubItem is null)
        {
            return;
        }

        foreach (var item in HardwareDecodingFlyoutSubItem.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = item.Tag is HardwareDecodingMode mode && mode == ViewModel.Settings.HardwareDecodingMode;
        }

        HardwareDecodingFlyoutSubItem.Text = $"Hardware Decode ({FormatHardwareDecodingLabel(ViewModel.Settings.HardwareDecodingMode)})";
    }

    private void UpdateDelayFlyoutLabels()
    {
        if (SubtitleDelayFlyoutSubItem is not null)
        {
            SubtitleDelayFlyoutSubItem.Text = $"Subtitle Delay ({_subtitleDelaySeconds:+0.00;-0.00;0.00}s)";
        }

        if (AudioDelayFlyoutSubItem is not null)
        {
            AudioDelayFlyoutSubItem.Text = $"Audio Delay ({_audioDelaySeconds:+0.00;-0.00;0.00}s)";
        }
    }

    private void RebuildAudioTrackFlyout()
    {
        if (AudioTracksFlyoutSubItem is null)
        {
            return;
        }

        AudioTracksFlyoutSubItem.Items.Clear();
        var audioTracks = _currentTracks
            .Where(track => track.Kind == MediaTrackKind.Audio)
            .OrderBy(track => track.Id)
            .ToList();
        if (audioTracks.Count == 0)
        {
            AudioTracksFlyoutSubItem.Items.Add(new MenuFlyoutItem
            {
                Text = "No alternate tracks",
                IsEnabled = false
            });
            return;
        }

        foreach (var track in audioTracks)
        {
            AudioTracksFlyoutSubItem.Items.Add(CreateTrackFlyoutItem(track, AudioTrackFlyoutItem_Click));
        }
    }

    private void RebuildEmbeddedSubtitleTrackFlyout()
    {
        if (EmbeddedSubtitleTracksFlyoutSubItem is null)
        {
            return;
        }

        EmbeddedSubtitleTracksFlyoutSubItem.Items.Clear();
        var hasSelectedEmbeddedTrack = _currentTracks.Any(track => track.Kind == MediaTrackKind.Subtitle && track.IsSelected);
        var offItem = new ToggleMenuFlyoutItem
        {
            Text = "Off",
            Tag = "off",
            IsChecked = !hasSelectedEmbeddedTrack
        };
        offItem.Click += EmbeddedSubtitleTrackFlyoutItem_Click;
        EmbeddedSubtitleTracksFlyoutSubItem.Items.Add(offItem);

        var subtitleTracks = _currentTracks
            .Where(track => track.Kind == MediaTrackKind.Subtitle)
            .OrderBy(track => track.Id)
            .ToList();
        if (subtitleTracks.Count == 0)
        {
            EmbeddedSubtitleTracksFlyoutSubItem.Items.Add(new MenuFlyoutSeparator());
            EmbeddedSubtitleTracksFlyoutSubItem.Items.Add(new MenuFlyoutItem
            {
                Text = "No embedded subtitle tracks",
                IsEnabled = false
            });
            return;
        }

        EmbeddedSubtitleTracksFlyoutSubItem.Items.Add(new MenuFlyoutSeparator());
        foreach (var track in subtitleTracks)
        {
            EmbeddedSubtitleTracksFlyoutSubItem.Items.Add(CreateTrackFlyoutItem(track, EmbeddedSubtitleTrackFlyoutItem_Click));
        }
    }

    private MenuFlyoutItem CreateTrackFlyoutItem(MediaTrackInfo track, RoutedEventHandler clickHandler)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = FormatTrackLabel(track),
            Tag = track.Id,
            IsChecked = track.IsSelected
        };
        item.Click += clickHandler;
        return item;
    }

    private void ApplySubtitleStyleSettings()
    {
        var style = ViewModel.Settings.SubtitleStyle;
        SourceSubtitleTextBlock.FontSize = style.SourceFontSize;
        SourceSubtitleTextBlock.LineHeight = Math.Max(style.SourceFontSize * 1.3, style.SourceFontSize + 4);
        SourceSubtitleTextBlock.Margin = new Thickness(0, 0, 0, style.DualSpacing);
        SourceSubtitleTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.SourceForegroundHex, ColorHelper.FromArgb(255, 241, 246, 251)));

        TranslatedSubtitleTextBlock.FontSize = style.TranslationFontSize;
        TranslatedSubtitleTextBlock.LineHeight = Math.Max(style.TranslationFontSize * 1.25, style.TranslationFontSize + 4);
        TranslatedSubtitleTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.TranslationForegroundHex, Colors.White));

        var overlayAlpha = (byte)Math.Clamp(Math.Round(style.BackgroundOpacity * 255), 0, 255);
        SubtitleOverlayBorder.Background = new SolidColorBrush(ColorHelper.FromArgb(overlayAlpha, 18, 23, 32));
        SubtitleOverlayBorder.Margin = new Thickness(0, 0, 0, style.BottomMargin);
        _subtitlePresenter.ApplyStyle(style);
    }

    private void UpdateSubtitleStyle(Func<SubtitleStyleSettings, SubtitleStyleSettings> updater, string statusMessage)
    {
        ViewModel.Settings = ViewModel.Settings with
        {
            SubtitleStyle = updater(ViewModel.Settings.SubtitleStyle)
        };

        ApplySubtitleStyleSettings();
        UpdateSubtitleVisibility();
        SaveCurrentSettings();
        ShowStatus(statusMessage);
    }

    private static string FormatTrackLabel(MediaTrackInfo track)
    {
        var language = string.IsNullOrWhiteSpace(track.Language) ? "und" : track.Language.ToUpperInvariant();
        var label = string.IsNullOrWhiteSpace(track.Title)
            ? $"{language} · Track {track.Id}"
            : $"{track.Title} ({language})";

        if (track.Kind == MediaTrackKind.Subtitle && !track.IsTextBased)
        {
            label += " · image-based";
        }

        return label;
    }

    private void ApplyTrackSelection(MediaTrackKind kind, int? selectedTrackId)
    {
        for (var index = 0; index < _currentTracks.Count; index++)
        {
            var track = _currentTracks[index];
            if (track.Kind != kind)
            {
                continue;
            }

            _currentTracks[index] = new MediaTrackInfo
            {
                Id = track.Id,
                FfIndex = track.FfIndex,
                Kind = track.Kind,
                Title = track.Title,
                Language = track.Language,
                Codec = track.Codec,
                IsEmbedded = track.IsEmbedded,
                IsSelected = selectedTrackId is not null && track.Id == selectedTrackId.Value,
                IsTextBased = track.IsTextBased
            };
        }
    }

    private void AspectRatioFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string aspectRatio })
        {
            return;
        }

        _selectedAspectRatio = aspectRatio;
        _ = _shellController.SetAspectRatioAsync(aspectRatio);
        UpdateAspectRatioFlyoutChecks();
        SaveCurrentSettings();
        ShowStatus($"Aspect ratio: {(aspectRatio == "-1" ? "fill" : aspectRatio)}.");
    }

    private void SubtitleRenderModeFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: SubtitleRenderMode mode })
        {
            return;
        }

        ApplySubtitleRenderMode(mode);
    }

    private void ApplySubtitleRenderMode(SubtitleRenderMode mode)
    {
        var translationActive = _subtitleWorkflowController.Snapshot.IsTranslationEnabled;
        if (translationActive && mode == SubtitleRenderMode.SourceOnly)
        {
            _subtitleSourceOnlyOverrideVideoPath = _subtitleWorkflowController.Snapshot.CurrentVideoPath;
            _lastNonOffSubtitleRenderMode = SubtitleRenderMode.SourceOnly;
            UpdateSubtitleRenderModeFlyoutChecks();
            UpdateSubtitleVisibility();
            UpdateOverlayControlState();
            ShowStatus("Subtitle mode: source only.");
            return;
        }

        _subtitleSourceOnlyOverrideVideoPath = null;
        if (mode != SubtitleRenderMode.Off)
        {
            _lastNonOffSubtitleRenderMode = mode;
        }

        ViewModel.Settings = ViewModel.Settings with
        {
            SubtitleRenderMode = mode
        };
        UpdateSubtitleRenderModeFlyoutChecks();
        UpdateSubtitleVisibility();
        UpdateOverlayControlState();
        SaveCurrentSettings();
        ShowStatus($"Subtitle mode: {FormatSubtitleRenderModeLabel(mode)}.");
    }

    private void HardwareDecodingFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: HardwareDecodingMode mode })
        {
            return;
        }

        ViewModel.Settings = ViewModel.Settings with
        {
            HardwareDecodingMode = mode
        };
        _ = _shellController.SetHardwareDecodingModeAsync(mode);
        UpdateHardwareDecodingFlyoutChecks();
        SaveCurrentSettings();
        ShowStatus($"Hardware decode: {FormatHardwareDecodingLabel(mode)}.");
    }

    private void AudioTrackFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: int trackId, Text: string label })
        {
            return;
        }

        _ = _shellController.SetAudioTrackAsync(trackId);
        ApplyTrackSelection(MediaTrackKind.Audio, trackId);
        RebuildAudioTrackFlyout();
        ShowStatus($"Selected audio track: {label}.");
    }

    private async void EmbeddedSubtitleTrackFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item)
        {
            return;
        }

        var currentPath = _shellController.CurrentPlaybackSnapshot.Path;
        if (item.Tag is string offValue && offValue == "off")
        {
            _ = _shellController.SetSubtitleTrackAsync(null);
            ApplyTrackSelection(MediaTrackKind.Subtitle, null);
            RebuildEmbeddedSubtitleTrackFlyout();
            if (ViewModel.SubtitleSource == SubtitlePipelineSource.EmbeddedTrack && !string.IsNullOrWhiteSpace(currentPath))
            {
                await _subtitleWorkflowController.LoadMediaSubtitlesAsync(currentPath);
            }

            ShowStatus("Embedded subtitle track disabled.");
            return;
        }

        if (item.Tag is not int trackId)
        {
            return;
        }

        var track = _currentTracks.FirstOrDefault(candidate => candidate.Kind == MediaTrackKind.Subtitle && candidate.Id == trackId);
        if (track is null)
        {
            return;
        }

        if (track.IsTextBased)
        {
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                ShowStatus("Open a video first.", true);
                return;
            }

            _ = _shellController.SetSubtitleTrackAsync(null);
            ApplyTrackSelection(MediaTrackKind.Subtitle, null);
            RebuildEmbeddedSubtitleTrackFlyout();
            var result = await _subtitleWorkflowController.ImportEmbeddedSubtitleTrackAsync(currentPath, track);
            ShowStatus(result.CueCount > 0
                ? $"Imported embedded subtitle track {track.Id}."
                : "Embedded subtitle import failed.",
                result.CueCount == 0);
            return;
        }

        _ = _shellController.SetSubtitleTrackAsync(trackId);
        ApplyTrackSelection(MediaTrackKind.Subtitle, trackId);
        RebuildEmbeddedSubtitleTrackFlyout();
        ShowStatus("Selected image-based embedded subtitle track for direct playback.");
    }

    private void AdjustSubtitleDelay(double delta)
    {
        _subtitleDelaySeconds += delta;
        _ = _shellController.SetSubtitleDelayAsync(_subtitleDelaySeconds);
        UpdateDelayFlyoutLabels();
        SaveCurrentSettings();
        ShowStatus($"Subtitle delay: {_subtitleDelaySeconds:+0.00;-0.00;0.00}s");
    }

    private void ResetSubtitleDelay()
    {
        _subtitleDelaySeconds = 0;
        _ = _shellController.SetSubtitleDelayAsync(_subtitleDelaySeconds);
        UpdateDelayFlyoutLabels();
        SaveCurrentSettings();
        ShowStatus("Subtitle delay reset.");
    }

    private void AdjustAudioDelay(double delta)
    {
        _audioDelaySeconds += delta;
        _ = _shellController.SetAudioDelayAsync(_audioDelaySeconds);
        UpdateDelayFlyoutLabels();
        SaveCurrentSettings();
        ShowStatus($"Audio delay: {_audioDelaySeconds:+0.00;-0.00;0.00}s");
    }

    private void ResetAudioDelay()
    {
        _audioDelaySeconds = 0;
        _ = _shellController.SetAudioDelayAsync(_audioDelaySeconds);
        UpdateDelayFlyoutLabels();
        SaveCurrentSettings();
        ShowStatus("Audio delay reset.");
    }

    private void SubtitleDelayBack_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(-0.05);

    private void SubtitleDelayForward_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(0.05);

    private void ResetSubtitleDelay_Click(object sender, RoutedEventArgs e) => ResetSubtitleDelay();

    private void AudioDelayBack_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(-0.05);

    private void AudioDelayForward_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(0.05);

    private void ResetAudioDelay_Click(object sender, RoutedEventArgs e) => ResetAudioDelay();

    private void ResumePlaybackToggleItem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Settings = ViewModel.Settings with
        {
            ResumeEnabled = ResumePlaybackToggleItem.IsChecked
        };
        _shellController.SetResumeTrackingEnabled(ResumePlaybackToggleItem.IsChecked);
        if (!ResumePlaybackToggleItem.IsChecked)
        {
            _shellController.ClearResumeHistory();
        }

        SaveCurrentSettings();
        ShowStatus(ResumePlaybackToggleItem.IsChecked ? "Resume playback enabled." : "Resume playback disabled.");
    }

    private void IncreaseSubtitleFont_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            SourceFontSize = Math.Min(style.SourceFontSize + 2, 44),
            TranslationFontSize = Math.Min(style.TranslationFontSize + 2, 48)
        },
        "Subtitle text enlarged.");

    private void DecreaseSubtitleFont_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            SourceFontSize = Math.Max(style.SourceFontSize - 2, 18),
            TranslationFontSize = Math.Max(style.TranslationFontSize - 2, 20)
        },
        "Subtitle text reduced.");

    private void IncreaseSubtitleBackground_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            BackgroundOpacity = Math.Min(style.BackgroundOpacity + 0.08, 0.95)
        },
        "Subtitle background increased.");

    private void DecreaseSubtitleBackground_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            BackgroundOpacity = Math.Max(style.BackgroundOpacity - 0.08, 0.15)
        },
        "Subtitle background reduced.");

    private void RaiseSubtitles_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            BottomMargin = Math.Min(style.BottomMargin + 10, 80)
        },
        "Subtitles raised.");

    private void LowerSubtitles_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            BottomMargin = Math.Max(style.BottomMargin - 10, 0)
        },
        "Subtitles lowered.");

    private void TranslationColorFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        string? colorHex = null;
        string? label = null;
        switch (sender)
        {
            case MenuFlyoutItem { Tag: string menuColorHex, Text: string menuLabel }:
                colorHex = menuColorHex;
                label = menuLabel;
                break;
            case Button { Tag: string buttonColorHex, Content: string buttonLabel }:
                colorHex = buttonColorHex;
                label = buttonLabel;
                break;
        }

        if (string.IsNullOrWhiteSpace(colorHex) || string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        UpdateSubtitleStyle(
            style => style with
            {
                TranslationForegroundHex = colorHex
            },
            $"Translation color: {label}.");
    }

    private async void EditShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var updatedProfile = await _credentialDialogService.EditShortcutsAsync(ViewModel.Settings.ShortcutProfile);
        if (updatedProfile is null)
        {
            return;
        }

        ViewModel.Settings = ViewModel.Settings with
        {
            ShortcutProfile = updatedProfile
        };
        RebuildShortcutBindings();
        SaveCurrentSettings();
        ShowStatus("Keyboard shortcuts updated.");
    }

    private async void ExportCurrentSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (!_subtitleWorkflowController.HasCurrentCues)
        {
            ShowStatus("No subtitles available to export.");
            return;
        }

        var exportPath = await _filePickerService.PickSaveFileAsync(
            "translated-subtitles",
            "SubRip subtitles",
            [".srt"]);
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            return;
        }

        _subtitleWorkflowController.ExportCurrentSubtitles(exportPath);
        ShowStatus($"Exported subtitles: {Path.GetFileName(exportPath)}");
    }

    private void AddPinnedRoot(string path)
    {
        if (ViewModel.Browser.Roots.Any(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ViewModel.Browser.Roots.Add(_libraryBrowserService.BuildRootNode(path));
        ViewModel.Browser.IsVisible = true;
        BrowserPaneToggle.IsChecked = true;
        RebuildLibraryTree();
        SyncPaneLayout(RootGrid.ActualWidth);
        ShowStatus($"Pinned root added: {path}");
    }

    private void RemovePinnedRoot(string path)
    {
        var existing = ViewModel.Browser.Roots.FirstOrDefault(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        ViewModel.Browser.Roots.Remove(existing);
        RebuildLibraryTree();
        if (ViewModel.Browser.Roots.Count == 0)
        {
            ViewModel.Browser.IsVisible = false;
            BrowserPaneToggle.IsChecked = false;
        }

        SyncPaneLayout(RootGrid.ActualWidth);
        ShowStatus($"Pinned root removed: {path}");
    }

    private async Task QueueFolderIntoPlaylistAsync(bool autoplay)
    {
        var folder = await _filePickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await ApplyQueueMutationAsync(_shellController.EnqueueFolder(folder, autoplay));
    }

    private async Task ApplyQueueMutationAsync(ShellQueueMediaResult result)
    {
        foreach (var folder in result.PinnedFolders)
        {
            AddPinnedRoot(folder);
        }

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ShowStatus(result.StatusMessage, result.IsError);
        }

        if (result.ItemToLoad is not null)
        {
            await LoadPlaybackItemAsync(result.ItemToLoad);
        }
    }

    private ShellLoadMediaOptions BuildShellLoadOptions()
    {
        return new ShellLoadMediaOptions
        {
            HardwareDecodingMode = ViewModel.Settings.HardwareDecodingMode,
            PlaybackRate = ViewModel.Transport.PlaybackRate,
            AspectRatio = _selectedAspectRatio,
            AudioDelaySeconds = _audioDelaySeconds,
            SubtitleDelaySeconds = _subtitleDelaySeconds,
            Volume = ViewModel.Transport.Volume,
            ResumeEnabled = ViewModel.Settings.ResumeEnabled,
            PreviousPlaybackState = _shellController.CurrentPlaybackSnapshot
        };
    }

    private static bool TryGetLibraryNodeFromElement(DependencyObject? source, out FrameworkElement anchor, out LibraryNode node)
    {
        var current = source;
        while (current is not null)
        {
            if (current is TreeViewItem item && item.Content is LibraryNode itemNode)
            {
                anchor = item;
                node = itemNode;
                return true;
            }

            if (current is FrameworkElement element && element.DataContext is LibraryNode dataNode)
            {
                anchor = element;
                node = dataNode;
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        anchor = null!;
        node = null!;
        return false;
    }

    private static void OpenInExplorer(string path, bool selectItem)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var startInfo = selectItem
            ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            : new ProcessStartInfo("explorer.exe", $"\"{path}\"");
        startInfo.UseShellExecute = true;
        Process.Start(startInfo);
    }

    private async Task LoadPlaybackItemAsync(PlaylistItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            return;
        }

        _subtitleSourceOnlyOverrideVideoPath = null;
        _isLanguageToolsAutoCollapseOverridden = false;
        _pendingAutoFitPath = item.Path;
        _lastAutoFitSignature = null;
        var loaded = await _shellController.LoadPlaybackItemAsync(
            item,
            BuildShellLoadOptions(),
            CancellationToken.None);
        if (!loaded)
        {
            return;
        }

        PlayerHost.RequestHostBoundsSync();
        UpdateWindowHeader();
    }

    private void ApplyQueueSnapshot(PlaybackQueueSnapshot snapshot)
    {
        ViewModel.Queue.NowPlayingItem = snapshot.NowPlayingItem;
        ViewModel.Queue.QueueItems.Clear();
        foreach (var item in snapshot.QueueItems)
        {
            ViewModel.Queue.QueueItems.Add(item);
        }

        ViewModel.Queue.HistoryItems.Clear();
        foreach (var item in snapshot.HistoryItems)
        {
            ViewModel.Queue.HistoryItems.Add(item);
        }

        if (ViewModel.Queue.SelectedQueueItem is not null
            && !snapshot.QueueItems.Any(item => string.Equals(item.Path, ViewModel.Queue.SelectedQueueItem.Path, StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Queue.SelectedQueueItem = null;
            PlaylistList.SelectedItem = null;
        }

        if (ViewModel.Queue.SelectedHistoryItem is not null
            && !snapshot.HistoryItems.Any(item => string.Equals(item.Path, ViewModel.Queue.SelectedHistoryItem.Path, StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Queue.SelectedHistoryItem = null;
            HistoryList.SelectedItem = null;
        }

        NowPlayingQueueTextBlock.Text = snapshot.NowPlayingItem?.DisplayName ?? "Nothing is playing.";
        PlaylistSummaryTextBlock.Text = snapshot.QueueItems.Count == 0
            ? "Queue is empty"
            : $"{snapshot.QueueItems.Count} item(s) up next";
        UpdateWindowHeader();
    }

    private void RebuildLibraryTree()
    {
        LibraryTree.RootNodes.Clear();
        foreach (var root in ViewModel.Browser.Roots)
        {
            LibraryTree.RootNodes.Add(CreateTreeNode(root));
        }
    }

    private TreeViewNode CreateTreeNode(LibraryNode model)
    {
        var node = new TreeViewNode
        {
            Content = model,
            IsExpanded = model.IsExpanded,
            HasUnrealizedChildren = model.IsFolder
        };

        if (model.Children.Count > 0)
        {
            node.HasUnrealizedChildren = false;
            foreach (var child in model.Children)
            {
                node.Children.Add(CreateTreeNode(child));
            }
        }

        return node;
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
        var showPlaylist = ViewModel.Queue.IsVisible && width >= GetPlaylistDrawerVisibilityThreshold();
        var browserWidth = GetPreferredBrowserDrawerWidth(width);
        var playlistWidth = GetPreferredPlaylistDrawerWidth(width);
        if (showBrowser && showPlaylist && width < 1500)
        {
            browserWidth = 260;
            playlistWidth = 288;
        }

        BrowserPane.Visibility = showBrowser ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPane.Visibility = showPlaylist ? Visibility.Visible : Visibility.Collapsed;
        BrowserColumn.Width = showBrowser ? new GridLength(browserWidth) : new GridLength(0);
        PlaylistColumn.Width = showPlaylist ? new GridLength(playlistWidth) : new GridLength(0);
        ShellContentGrid.ColumnSpacing = showBrowser && showPlaylist
            ? 16
            : showBrowser || showPlaylist
                ? 12
                : 0;
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
        var hideStatus = height < 760;
        var compactLayout = height < 760 || narrowWindow;
        var hideLanguageTools = height < 640;
        var hideTransport = height < 580;
        var hideTimeline = height < 520;

        StatusInfoBar.Visibility = hideStatus ? Visibility.Collapsed : Visibility.Visible;
        TimelinePane.Visibility = hideTimeline ? Visibility.Collapsed : Visibility.Visible;
        TransportPane.Visibility = hideTransport ? Visibility.Collapsed : Visibility.Visible;
        LanguageToolsPane.Visibility = hideLanguageTools ? Visibility.Collapsed : Visibility.Visible;
        CenterStageGrid.RowSpacing = compactLayout ? 8 : 10;
        TimelinePane.Padding = compactLayout ? new Thickness(12, 8, 12, 8) : new Thickness(14, 10, 14, 10);
        TransportPane.Padding = compactLayout ? new Thickness(8, 4, 8, 4) : new Thickness(10, 6, 10, 6);
        LanguageToolsPane.Padding = compactLayout ? new Thickness(14, 12, 14, 14) : new Thickness(16, 14, 16, 16);
        ShellContentGrid.Padding = compactLayout ? new Thickness(14, 8, 14, 12) : new Thickness(20, 10, 20, 18);
        PlayerPane.MinHeight = compactLayout ? 250 : 320;
        UpdatePortraitVideoLanguageToolsState();
    }

    private static double GetBrowserDrawerVisibilityThreshold() => 980;

    private static double GetPlaylistDrawerVisibilityThreshold() => 1320;

    private static double GetPreferredBrowserDrawerWidth(double width)
        => width >= 1560 ? 312 : 276;

    private static double GetPreferredPlaylistDrawerWidth(double width)
        => width >= 1560 ? 336 : 300;

    private void TryApplyStandardAutoFit()
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Standard)
        {
            return;
        }

        var playbackSnapshot = _shellController.CurrentPlaybackSnapshot;
        var sourcePath = playbackSnapshot.Path;
        if (string.IsNullOrWhiteSpace(sourcePath) || (_pendingAutoFitPath is not null && !string.Equals(_pendingAutoFitPath, sourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var displayWidth = playbackSnapshot.VideoDisplayWidth > 0 ? playbackSnapshot.VideoDisplayWidth : playbackSnapshot.VideoWidth;
        var displayHeight = playbackSnapshot.VideoDisplayHeight > 0 ? playbackSnapshot.VideoDisplayHeight : playbackSnapshot.VideoHeight;
        if (displayWidth <= 0 || displayHeight <= 0 || PlayerPane.ActualWidth <= 0 || PlayerHost.View.ActualHeight <= 0)
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
        var stageWidth = Math.Max(PlayerHost.View.ActualWidth, 1);
        var stageHeight = Math.Max(PlayerHost.View.ActualHeight, 1);
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
            drawerPreservingWidth = Math.Max(
                drawerPreservingWidth,
                GetPlaylistDrawerVisibilityThreshold());
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

    private async void FullscreenExitAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen)
        {
            return;
        }

        args.Handled = true;
        await ExitFullscreenAsync();
    }

    private async Task SetWindowModeAsync(PlaybackWindowMode mode)
    {
        await _windowModeService.SetModeAsync(mode);
        ApplyWindowModeChrome(mode);
        PlayerHost.RequestHostBoundsSync();
        SyncWindowModeButtons(mode);
        UpdateOverlayControlState();
        if (!_isInitializingShellState)
        {
            SaveCurrentSettings();
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
        AppTitleBar.Visibility = isNonStandard ? Visibility.Collapsed : Visibility.Visible;
        ShellCommandBar.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        StatusInfoBar.Visibility = isNonStandard ? Visibility.Collapsed : Visibility.Visible;
        TimelinePane.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        TransportPane.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        LanguageToolsPane.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        ShellContentGrid.Padding = isNonStandard ? new Thickness(0) : new Thickness(20, 10, 20, 18);
        ShellContentGrid.ColumnSpacing = isNonStandard ? 0 : ShellContentGrid.ColumnSpacing;
        PlayerPane.Padding = isNonStandard ? new Thickness(0) : new Thickness(18);
        PlayerPane.BorderThickness = isNonStandard ? new Thickness(0) : new Thickness(1);
        PlayerPane.CornerRadius = isNonStandard ? new CornerRadius(0) : new CornerRadius(24);
        DecoderBadge.Visibility = mode == PlaybackWindowMode.Fullscreen ? Visibility.Collapsed : Visibility.Visible;
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
        overlayWindow.SeekBackButton.Click += SeekBack_Click;
        overlayWindow.SeekForwardButton.Click += SeekForward_Click;
        overlayWindow.ExitFullscreenButton.Click += ExitFullscreenOverlayButton_Click;
        FullscreenPositionSlider = overlayWindow.PositionSlider;
        FullscreenPositionSlider.ValueChanged += FullscreenPositionSlider_ValueChanged;
        AttachScrubberHandlers(FullscreenPositionSlider);
        FullscreenCurrentTimeTextBlock = overlayWindow.CurrentTimeTextBlock;
        FullscreenDurationTextBlock = overlayWindow.DurationTextBlock;
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

        if (OverlayPlayPauseButton is not null)
        {
            OverlayPlayPauseButton.Content = ViewModel.Transport.IsPaused ? "Play" : "Pause";
        }

        if (OverlaySubtitleToggleButton is not null)
        {
            OverlaySubtitleToggleButton.Content = ViewModel.Settings.SubtitleRenderMode == SubtitleRenderMode.Off
                ? "Subtitles Off"
                : "Subtitles On";
        }
    }

    private void PositionFullscreenOverlay()
    {
        _stageCoordinator.HandleStageLayoutChanged();
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

    private void UpdateSubtitleOverlay(SubtitleWorkflowSnapshot snapshot)
    {
        var presentation = _subtitleWorkflowController.GetOverlayPresentation(
            ViewModel.Settings.SubtitleRenderMode,
            subtitlesVisible: ViewModel.Settings.SubtitleRenderMode != SubtitleRenderMode.Off,
            sourceOnlyOverrideForCurrentVideo: HasSourceOnlyOverrideForCurrentVideo(snapshot));
        ViewModel.SubtitleOverlay.ShowSource = !string.IsNullOrWhiteSpace(presentation.SecondaryText);
        ViewModel.SubtitleOverlay.SourceText = presentation.SecondaryText;
        ViewModel.SubtitleOverlay.TranslationText = presentation.PrimaryText;
        UpdateSubtitleVisibility();
    }

    private void UpdateSubtitleVisibility()
    {
        var subtitlesEnabled = ViewModel.Settings.SubtitleRenderMode != SubtitleRenderMode.Off;
        var showSource = subtitlesEnabled && ViewModel.SubtitleOverlay.ShowSource && !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.SourceText);
        var showPrimary = subtitlesEnabled && !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.TranslationText);

        SourceSubtitleTextBlock.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        SourceSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.SourceText;

        TranslatedSubtitleTextBlock.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
        TranslatedSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.TranslationText;
        SubtitleOverlayBorder.Visibility = Visibility.Collapsed;
        _stageCoordinator.PresentSubtitles(
            new SubtitlePresentationModel
            {
                IsVisible = showSource || showPrimary,
                PrimaryText = showPrimary ? ViewModel.SubtitleOverlay.TranslationText : string.Empty,
                SecondaryText = showSource ? ViewModel.SubtitleOverlay.SourceText : string.Empty
            },
            ViewModel.Settings.SubtitleStyle,
            !string.IsNullOrWhiteSpace(_shellController.CurrentPlaybackSnapshot.Path));
        UpdateOverlayControlState();
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

    private void AttachScrubberHandlers(Slider slider)
    {
        slider.PointerPressed += Scrubber_PointerPressed;
        slider.PointerReleased += Scrubber_PointerReleased;
        slider.PointerCaptureLost += Scrubber_PointerCaptureLost;
    }

    private void Scrubber_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPositionScrubbing = true;
        _activeScrubber = sender as Slider;
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            EnsureStageOverlayControls();
            _stageCoordinator.HandleScrubbingChanged(true);
            ShowFullscreenOverlay();
        }
    }

    private void Scrubber_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        EndScrubbing(sender);
    }

    private void Scrubber_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        EndScrubbing(sender);
    }

    private void EndScrubbing(object sender)
    {
        if (sender is Slider slider)
        {
            _ = _shellController.SeekAsync(TimeSpan.FromSeconds(slider.Value));
        }

        _isPositionScrubbing = false;
        _activeScrubber = null;
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            _stageCoordinator.HandleScrubbingChanged(false);
            ShowFullscreenOverlay();
        }
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

        if (AppTitleBar is not null)
        {
            yield return AppTitleBar;
        }

        if (ShellCommandBar is not null)
        {
            yield return ShellCommandBar;
        }

        if (StatusInfoBar is not null)
        {
            yield return StatusInfoBar;
        }

        if (ShellContentGrid is not null)
        {
            yield return ShellContentGrid;
        }

        if (BrowserPane is not null)
        {
            yield return BrowserPane;
        }

        if (PlayerPane is not null)
        {
            yield return PlayerPane;
        }

        if (PlaylistPane is not null)
        {
            yield return PlaylistPane;
        }

        if (PlaylistList is not null)
        {
            yield return PlaylistList;
        }

        if (TransportPane is not null)
        {
            yield return TransportPane;
        }
    }

    private bool IsPlaylistDropTarget(object sender)
        => ReferenceEquals(sender, PlaylistPane) || ReferenceEquals(sender, PlaylistList);

    private async Task<bool> TryHandleLibraryQueueDropAsync(object sender, DataPackageView dataView)
    {
        if (!IsPlaylistDropTarget(sender) || !dataView.Contains(LibraryQueueDragFormat))
        {
            return false;
        }

        var data = await dataView.GetDataAsync(LibraryQueueDragFormat);
        if (data is not string rawPaths)
        {
            return false;
        }

        var paths = rawPaths
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return true;
        }

        var result = _shellController.AddToQueue(paths);
        ShowStatus(result.StatusMessage
            ?? (paths.Length == 1
                ? $"Queued {Path.GetFileName(paths[0])}."
                : $"Queued {paths.Length} item(s) from the library."));
        return true;
    }

    private static bool IsAppStillForeground()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == Environment.ProcessId;
    }

    private async Task PrepareForTranscriptionRefreshAsync()
    {
        var result = await _shellController.PrepareForTranscriptionRefreshAsync(
            _subtitleWorkflowController.Snapshot,
            _shellController.CurrentPlaybackSnapshot,
            CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ViewModel.Transport.IsPaused = true;
            UpdatePlayPauseButtonVisual();
            UpdateOverlayControlState();
            ShowStatus(result.StatusMessage);
        }
    }

    private async Task ApplyCaptionStartupGateAsync(SubtitleWorkflowSnapshot snapshot)
    {
        var result = await _shellController.EvaluateCaptionStartupGateAsync(
            snapshot,
            _shellController.CurrentPlaybackSnapshot,
            CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ShowStatus(result.StatusMessage);
        }
        UpdatePlayPauseButtonVisual();
        UpdateOverlayControlState();
    }

    private void UpdatePositionSurfaces(TimeSpan position, TimeSpan duration)
    {
        var maximumSeconds = Math.Max(duration.TotalSeconds, 1);
        if (!_isPositionScrubbing)
        {
            _suppressPositionSliderChanges = true;
            PositionSlider.Maximum = maximumSeconds;
            PositionSlider.Value = Math.Min(position.TotalSeconds, maximumSeconds);
            _suppressPositionSliderChanges = false;

            if (FullscreenPositionSlider is not null)
            {
                _suppressFullscreenSliderChanges = true;
                FullscreenPositionSlider.Maximum = maximumSeconds;
                FullscreenPositionSlider.Value = Math.Min(position.TotalSeconds, maximumSeconds);
                _suppressFullscreenSliderChanges = false;
            }
        }

        UpdateScrubTimeLabels(_isPositionScrubbing ? _activeScrubber?.Value ?? position.TotalSeconds : position.TotalSeconds, maximumSeconds);
    }

    private void UpdateScrubTimeLabels(double currentSeconds, double totalSeconds)
    {
        if (FullscreenCurrentTimeTextBlock is null || FullscreenDurationTextBlock is null)
        {
            return;
        }

        var current = TimeSpan.FromSeconds(Math.Max(currentSeconds, 0));
        var total = TimeSpan.FromSeconds(Math.Max(totalSeconds, 0));
        FullscreenCurrentTimeTextBlock.Text = FormatPlaybackClock(current);
        FullscreenDurationTextBlock.Text = total > TimeSpan.Zero ? FormatPlaybackClock(total) : "00:00";
    }

    private static string FormatPlaybackClock(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private void ShowStatus(string message, bool isError = false)
    {
        ViewModel.StatusMessage = message;
        ViewModel.IsStatusOpen = true;
        StatusInfoBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
        ViewModel.StatusFeed.Insert(0, message);
        if (ViewModel.StatusFeed.Count > 20)
        {
            ViewModel.StatusFeed.RemoveAt(ViewModel.StatusFeed.Count - 1);
        }
    }

    private readonly record struct ResolvedShortcutBinding(string CommandId, VirtualKey Key, bool Ctrl, bool Alt, bool Shift)
    {
        public bool Matches(ShortcutKeyInput input)
        {
            return input.Key == Key
                && input.Ctrl == Ctrl
                && input.Alt == Alt
                && input.Shift == Shift;
        }
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

    private static string FormatSubtitleRenderModeLabel(SubtitleRenderMode mode)
    {
        return mode switch
        {
            SubtitleRenderMode.Off => "off",
            SubtitleRenderMode.SourceOnly => "source only",
            SubtitleRenderMode.TranslationOnly => "translation only",
            SubtitleRenderMode.Dual => "dual",
            _ => "translation only"
        };
    }

    private static string FormatHardwareDecodingLabel(HardwareDecodingMode mode)
    {
        return mode switch
        {
            HardwareDecodingMode.AutoSafe => "Auto Safe",
            HardwareDecodingMode.D3D11 => "D3D11",
            HardwareDecodingMode.Nvdec => "NVDEC",
            HardwareDecodingMode.Software => "Software",
            _ => "Auto Safe"
        };
    }

    private static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var value = hex.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value[1..];
        }

        try
        {
            return value.Length switch
            {
                6 => ColorHelper.FromArgb(
                    255,
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16)),
                8 => ColorHelper.FromArgb(
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16),
                    Convert.ToByte(value.Substring(6, 2), 16)),
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }
}
