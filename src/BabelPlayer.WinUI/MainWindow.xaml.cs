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
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;
using Windows.System;
using Windows.UI;

namespace BabelPlayer.WinUI;

public sealed partial class MainWindow : Window
{
    private enum LanguageToolsPanelLayoutMode
    {
        Unknown,
        Wide,
        Medium,
        Narrow
    }

    private const string LibraryQueueDragFormat = "BabelPlayer/LibraryQueuePaths";
    private readonly PlaybackQueueController _playbackQueueController = new();
    private readonly ISubtitleWorkflowShellService _subtitleWorkflowService;
    private readonly IPlaybackBackend _playbackBackend;
    private readonly PlaybackBackendCoordinator _playbackBackendCoordinator;
    private readonly IShellPreferencesService _shellPreferencesService;
    private readonly IShellLibraryService _shellLibraryService;
    private readonly ShellProjectionService _shellProjectionService;
    private readonly IQueueProjectionReader _queueProjectionReader;
    private readonly IQueueCommands _queueCommands;
    private readonly IShellPlaybackCommands _shellPlaybackCommands;
    private readonly ICredentialSetupService _credentialSetupService;
    private readonly IShortcutProfileService _shortcutProfileService;
    private readonly IShortcutCommandExecutor _shortcutCommandExecutor;
    private readonly IDisposable _shellControllerLifetime;
    private readonly IVideoPresenter _videoPresenter;
    private readonly ISubtitlePresenter _subtitlePresenter;
    private readonly IFilePickerService _filePickerService;
    private readonly WinUIWindowModeService _windowModeService;
    private readonly WinUICredentialDialogService _credentialDialogService;
    private readonly IRuntimeBootstrapService _runtimeBootstrapService;
    private readonly StageCoordinator _stageCoordinator;
    private readonly IBabelLogger _logger;
    private readonly IBabelLogger _statusLogger;
    private readonly IAppDiagnosticsContext _diagnosticsContext;
    private readonly List<MediaTrackInfo> _currentTracks = [];
    private ShellProjectionSnapshot _currentShellProjection = new();
    private bool _suppressPositionSliderChanges;
    private bool _suppressFullscreenSliderChanges;
    private bool _suppressWorkflowControlEvents;
    private bool _suppressWindowModeButtonChanges;
    private bool _isPositionScrubbing;
    private bool _isInitializingShellState;
    private bool _isLanguageToolsExpanded = true;
    private bool _autoCollapsedLanguageToolsForPortraitVideo;
    private bool _isLanguageToolsAutoCollapseOverridden;
    private bool _hasAttemptedSystemBackdrop;
    private bool _shellDropTargetsInitialized;
    private bool _isLibraryDragOperationInProgress;
    private LanguageToolsPanelLayoutMode _languageToolsPanelLayoutMode;
    private Slider? _activeScrubber;
    private string? _pendingAutoFitPath;
    private string? _lastAutoFitSignature;
    private string? _pendingLibraryLoadPath;
    private LibraryEntrySnapshot? _selectedLibraryNode;
    private MicaBackdrop? _micaBackdrop;
    private readonly Dictionary<string, ResolvedShortcutBinding> _resolvedShortcutBindings = new(StringComparer.OrdinalIgnoreCase);
    private ShellLibrarySnapshot _currentLibrarySnapshot = new();
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
    private Grid UnifiedHeaderBar = null!;
    private Grid ShellContentGrid = null!;
    private Grid CenterStageGrid = null!;
    private Grid PlaybackLayoutRoot = null!;
    private Grid PlaybackStage = null!;
    private Border VideoStageSurface = null!;
    private Border BrowserPane = null!;
    private Border PlaylistPane = null!;
    private Border PlayerPane = null!;
    private Border TimelinePane = null!;
    private Border TransportPane = null!;
    private Border LanguageToolsPane = null!;
    private Border LanguageToolsContentBorder = null!;
    private Border LanguageToolsTranscriptionGroup = null!;
    private Border LanguageToolsTranslationGroup = null!;
    private Border LanguageToolsSubtitlesGroup = null!;
    private Border DecoderBadge = null!;
    private Border SubtitleOverlayBorder = null!;
    private Border StatusOverlayBorder = null!;
    private ColumnDefinition BrowserColumn = null!;
    private ColumnDefinition PlaylistColumn = null!;
    private TreeView LibraryTree = null!;
    private PlaybackHostAdapter PlayerHost = null!;
    private TextBlock HardwareDecoderTextBlock = null!;
    private TextBlock SourceSubtitleTextBlock = null!;
    private TextBlock TranslatedSubtitleTextBlock = null!;
    private TextBlock StatusOverlayTextBlock = null!;
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
    private bool _suppressVolumeSliderChanges;
    private bool _suppressPlaybackSpeedSliderChanges;
    private double _lastLoggedPlaybackStageWidth = -1;
    private double _lastLoggedPlaybackStageHeight = -1;
    private Button LanguageToolsToggleButton = null!;
    private Grid LanguageToolsGroupsGrid = null!;
    private ToggleButton LanguageToolsSubtitleToggleButton = null!;
    private TextBlock LanguageToolsSubtitleStatusTextBlock = null!;
    private ComboBox SubtitleModeComboBox = null!;
    private ComboBox TranscriptionModelComboBox = null!;
    private ComboBox TranslationModelComboBox = null!;
    private ToggleSwitch TranslationToggleSwitch = null!;
    private ToggleSwitch AutoTranslateToggleSwitch = null!;
    private TextBlock SubtitleDelayValueText = null!;
    private Slider PlaybackSpeedSlider = null!;
    private TextBlock PlaybackSpeedValueText = null!;
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
    private DropDownButton OverlaySubtitleModeButton = null!;
    private DropDownButton OverlaySubtitleStyleButton = null!;
    private Button OverlayPipButton = null!;
    private Button OverlayImmersiveButton = null!;
    private DropDownButton OverlaySettingsButton = null!;
    private Button OverlayExitFullscreenButton = null!;
    private Slider FullscreenPositionSlider = null!;
    private TextBlock FullscreenCurrentTimeTextBlock = null!;
    private TextBlock FullscreenDurationTextBlock = null!;
    private Grid RootGrid = null!;
    private readonly DispatcherTimer _statusOverlayTimer = new();

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
            SuppressDialogPresentation);
        _filePickerService = dependencies.FilePickerService;
        _windowModeService = dependencies.WindowModeService;
        _credentialDialogService = dependencies.CredentialDialogService;
        _runtimeBootstrapService = dependencies.RuntimeBootstrapService;
        _diagnosticsContext = dependencies.DiagnosticsContext;
        _logger = dependencies.LogFactory.CreateLogger("shell.window");
        _statusLogger = dependencies.LogFactory.CreateLogger("shell.status");
        _subtitleWorkflowService = dependencies.SubtitleWorkflowService;
        _playbackBackend = dependencies.PlaybackBackend;
        _playbackBackendCoordinator = dependencies.PlaybackBackendCoordinator;
        _videoPresenter = dependencies.VideoPresenter;
        _subtitlePresenter = dependencies.SubtitlePresenter;
        _shellPreferencesService = dependencies.ShellPreferencesService;
        _shellLibraryService = dependencies.ShellLibraryService;
        _shellProjectionService = dependencies.ShellProjectionService;
        _queueProjectionReader = dependencies.QueueProjectionReader;
        _queueCommands = dependencies.QueueCommands;
        _shellPlaybackCommands = dependencies.ShellPlaybackCommands;
        _credentialSetupService = dependencies.CredentialSetupService;
        _shortcutProfileService = dependencies.ShortcutProfileService;
        _shortcutCommandExecutor = dependencies.ShortcutCommandExecutor;
        _shellControllerLifetime = dependencies.ShellControllerLifetime;
        _statusOverlayTimer.Interval = TimeSpan.FromSeconds(4);
        _statusOverlayTimer.Tick += StatusOverlayTimer_Tick;
        BuildShell();
        _stageCoordinator = dependencies.StageCoordinator;

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
        PlayerHost.InputActivity += PlayerHost_InputActivity;
        PlayerHost.FullscreenExitRequested += PlayerHost_FullscreenExitRequested;
        PlayerHost.ShortcutKeyPressed += PlayerHost_ShortcutKeyPressed;
        _subtitleWorkflowService.StatusChanged += SubtitleWorkflow_StatusChanged;
        _subtitleWorkflowService.SnapshotChanged += SubtitleWorkflow_SnapshotChanged;
        _shellLibraryService.SnapshotChanged += ShellLibraryService_SnapshotChanged;
        _shellProjectionService.ProjectionChanged += ShellProjectionService_ProjectionChanged;
        _queueProjectionReader.QueueSnapshotChanged += ShellController_QueueSnapshotChanged;

        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;

        InitializeShellState();
        _diagnosticsContext.UpdateWindowMode(_windowModeService.CurrentMode.ToString());
        ApplyShellProjection(_shellProjectionService.Current);
        ApplyQueueSnapshot(_queueProjectionReader.QueueSnapshot);
        _logger.LogInfo("Main window initialized.");
        FireAndForget(_subtitleWorkflowService.InitializeAsync());
    }

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

        PlayerHost = new PlaybackHostAdapter(_playbackBackend, _videoPresenter);
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

    private void InitializeShellState()
    {
        _isInitializingShellState = true;
        var preferences = _shellPreferencesService.Current;
        ApplyPreferencesSnapshot(preferences);
        ApplyLibrarySnapshot(_shellLibraryService.Current);
        RebuildShortcutBindings();
        ViewModel.WindowTitle = "Babel Player";
        ViewModel.WindowSubtitle = string.Empty;
        ViewModel.StatusMessage = "Open local media or subtitles to start playback.";
        ViewModel.IsStatusOpen = false;
        ViewModel.ActiveHardwareDecoder = "mpv ready";
        ViewModel.SubtitleOverlay.TranslationText = "Drop a file or choose Open to start playback.";
        ViewModel.SelectedTranscriptionLabel = _subtitleWorkflowService.Current.SelectedTranscriptionLabel;
        ViewModel.SelectedTranslationLabel = _subtitleWorkflowService.Current.SelectedTranslationLabel;
        TranslatedSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.TranslationText;
        StatusInfoBar.IsOpen = false;
        StatusInfoBar.Message = ViewModel.StatusMessage;

        ThemeToggleMenuItem.IsChecked = true;
        ApplyTheme(isDark: true);
        VolumeSlider.Value = preferences.VolumeLevel * 100d;
        MuteToggleButton.IsChecked = preferences.IsMuted;
        PlayerHost.SetPreferredAudioState(preferences.VolumeLevel, preferences.IsMuted);
        _logger.LogInfo(
            "Configured preferred startup audio state.",
            BabelLogContext.Create(("volume", preferences.VolumeLevel), ("muted", preferences.IsMuted)));
        ResumePlaybackToggleItem.IsChecked = preferences.ResumeEnabled;
        SetPlaybackRate(preferences.PlaybackRate, persistSettings: false, showStatus: false);
        _windowModeService.EnsureInitialStandardBounds();
        _windowModeService.SetModeAsync(PlaybackWindowMode.Standard).GetAwaiter().GetResult();
        ApplyWindowModeChrome(PlaybackWindowMode.Standard);
        SyncWindowModeButtons(PlaybackWindowMode.Standard);
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

        RebuildLibraryTree();
        SyncPaneLayout(RootGrid.ActualWidth);
        ApplyAdaptiveStandardLayout(RootGrid.ActualHeight);
        PlayerHost.RequestHostBoundsSync();
        UpdateWindowHeader();
        _isInitializingShellState = false;
    }

    private void ApplyPreferencesSnapshot(ShellPreferencesSnapshot snapshot)
    {
        ViewModel.Settings = snapshot;
        ViewModel.Browser.IsVisible = snapshot.ShowBrowserPanel;
        ViewModel.Queue.IsVisible = snapshot.ShowPlaylistPanel;
        ViewModel.Transport.Volume = snapshot.VolumeLevel;
        ViewModel.Transport.IsMuted = snapshot.IsMuted;
        ViewModel.Transport.PlaybackRate = snapshot.PlaybackRate;
        ViewModel.SubtitleOverlay.ShowSource = snapshot.ShowSubtitleSource;
        SyncSubtitleDelayValueText();
    }

    private void ApplyLibrarySnapshot(ShellLibrarySnapshot snapshot)
    {
        _currentLibrarySnapshot = snapshot;
        ViewModel.Browser.Roots.Clear();
        foreach (var root in snapshot.Roots)
        {
            ViewModel.Browser.Roots.Add(root);
        }

        RebuildLibraryTree();
        if (_selectedLibraryNode is not null
            && !ContainsLibraryPath(snapshot.Roots, _selectedLibraryNode.Path))
        {
            _selectedLibraryNode = null;
        }
    }

    private static bool ContainsLibraryPath(IEnumerable<LibraryEntrySnapshot> roots, string path)
    {
        foreach (var root in roots)
        {
            if (string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase)
                || ContainsLibraryPath(root.Children, path))
            {
                return true;
            }
        }

        return false;
    }

    private void ShellLibraryService_SnapshotChanged(ShellLibrarySnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyLibrarySnapshot(snapshot);
            SyncPaneLayout(RootGrid.ActualWidth);
            UpdateWindowHeader();
        });
    }

    private void UpdateWindowHeader()
    {
        var activeMediaTitle = GetActiveWindowSubtitle();
        ViewModel.WindowTitle = "Babel Player";
        ViewModel.WindowSubtitle = activeMediaTitle ?? string.Empty;
        WindowTitleTextBlock.Text = ViewModel.WindowTitle;
        WindowSubtitleTextBlock.Text = ViewModel.WindowSubtitle;
        WindowSubtitleTextBlock.Visibility = string.IsNullOrWhiteSpace(ViewModel.WindowSubtitle)
            ? Visibility.Collapsed
            : Visibility.Visible;
        Title = activeMediaTitle is null ? ViewModel.WindowTitle : $"{ViewModel.WindowTitle} - {activeMediaTitle}";
    }

    private string? GetActiveWindowSubtitle()
    {
        if (_queueProjectionReader.NowPlayingItem is { DisplayName: { Length: > 0 } currentItemName })
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
        _logger.LogInfo("Main window closing.");
        SystemBackdrop = null;
        _micaBackdrop = null;
        _shellPlaybackCommands.FlushResumeTracking();
        PersistLayoutPreferences();
        PersistPlaybackDefaults();
        PersistAudioStatePreferences();
        _stageCoordinator.Dispose();
        _subtitleWorkflowService.StatusChanged -= SubtitleWorkflow_StatusChanged;
        _subtitleWorkflowService.SnapshotChanged -= SubtitleWorkflow_SnapshotChanged;
        _shellLibraryService.SnapshotChanged -= ShellLibraryService_SnapshotChanged;
        _shellProjectionService.ProjectionChanged -= ShellProjectionService_ProjectionChanged;
        _queueProjectionReader.QueueSnapshotChanged -= ShellController_QueueSnapshotChanged;
        _shellProjectionService.Dispose();
        (_subtitlePresenter as IDisposable)?.Dispose();
        _subtitleWorkflowService.Dispose();
        _shellControllerLifetime.Dispose();
        _playbackBackendCoordinator.Dispose();
        FireAndForget(_playbackBackend.DisposeAsync());
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

    private void PersistLayoutPreferences()
    {
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyLayoutChange(new ShellLayoutPreferencesChange(
            ViewModel.Browser.IsVisible,
            ViewModel.Queue.IsVisible,
            _windowModeService.CurrentMode)));
    }

    private void PersistPlaybackDefaults()
    {
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyPlaybackDefaultsChange(new ShellPlaybackDefaultsChange(
            ViewModel.Settings.HardwareDecodingMode,
            ViewModel.Transport.PlaybackRate,
            ViewModel.Settings.AudioDelaySeconds,
            ViewModel.Settings.SubtitleDelaySeconds,
            ViewModel.Settings.AspectRatio)));
    }

    private void PersistSubtitlePresentationPreferences()
    {
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplySubtitlePresentationChange(new ShellSubtitlePresentationChange(
            ViewModel.Settings.SubtitleRenderMode,
            ViewModel.Settings.SubtitleStyle)));
    }

    private void PersistAudioStatePreferences()
    {
        var volume = Math.Clamp((VolumeSlider?.Value ?? (ViewModel.Transport.Volume * 100d)) / 100d, 0, 1);
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyAudioStateChange(new ShellAudioStateChange(
            volume,
            MuteToggleButton?.IsChecked == true)));
    }

    private void PersistShortcutProfile(ShortcutProfile profile)
    {
        _shortcutProfileService.ApplyShortcutProfileChange(profile);
        ApplyPreferencesSnapshot(_shellPreferencesService.Current);
    }

    private void PersistResumeEnabledPreference(bool resumeEnabled)
    {
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyResumeEnabledChange(new ShellResumeEnabledChange(resumeEnabled)));
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var files = await _filePickerService.PickMediaFilesAsync();
        await ApplyQueueMutationAsync(_queueCommands.EnqueueFiles(files, autoplay: true));
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

        var result = _shellLibraryService.PinRoot(folder);
        if (!result.IsError)
        {
            ViewModel.Browser.IsVisible = true;
            BrowserPaneToggle.IsChecked = true;
            PersistLayoutPreferences();
        }

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ShowStatus(result.StatusMessage, result.IsError);
        }
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
        var result = await _subtitleWorkflowService.ImportExternalSubtitlesAsync(subtitlePath, autoLoaded: false);
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
        var result = _subtitleWorkflowService.ToggleSubtitleVisibility(ViewModel.Settings.SubtitleRenderMode);
        var subtitlesEnabled = currentMode != SubtitleRenderMode.Off;
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplySubtitlePresentationChange(
            new ShellSubtitlePresentationChange(result.RequestedRenderMode, ViewModel.Settings.SubtitleStyle)));

        UpdateSubtitleVisibility();
        UpdateSubtitleRenderModeFlyoutChecks();
        UpdateOverlayControlState();
        ShowStatus(subtitlesEnabled ? "Subtitles hidden." : "Subtitles shown.");
    }

    private SubtitleRenderMode GetEffectiveSubtitleRenderMode()
    {
        return _subtitleWorkflowService.GetEffectiveRenderMode(ViewModel.Settings.SubtitleRenderMode);
    }

    private async void PlaylistList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItem item)
        {
            await ApplyQueueMutationAsync(_queueCommands.PlayNow(item.Path));
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
            await ApplyQueueMutationAsync(_queueCommands.PlayNow(item.Path));
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

        _queueCommands.RemoveQueueItemAt(PlaylistList.SelectedIndex);
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _queueCommands.ClearQueue();
        ShowStatus("Queue cleared.");
    }

    private async void PreviousTrack_Click(object sender, RoutedEventArgs e)
    {
        await LoadPlaybackItemAsync(_queueCommands.MovePrevious());
    }

    private async void NextTrack_Click(object sender, RoutedEventArgs e)
    {
        await LoadPlaybackItemAsync(_queueCommands.MoveNext());
    }

    private void SeekBack_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        FireAndForget(_shellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(-10)));
    }

    private void SeekForward_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        FireAndForget(_shellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(10)));
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        if (ViewModel.Transport.IsPaused)
        {
            FireAndForget(_shellPlaybackCommands.PlayAsync());
            ShowStatus("Playback resumed.");
            return;
        }

        FireAndForget(_shellPlaybackCommands.PauseAsync());
        ShowStatus("Playback paused.");
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        FireAndForget(_shellPlaybackCommands.StepFrameAsync(forward: false));
        ViewModel.Transport.IsPaused = true;
        ShowStatus("Stepped to previous frame.");
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        FireAndForget(_shellPlaybackCommands.StepFrameAsync(forward: true));
        ViewModel.Transport.IsPaused = true;
        ShowStatus("Stepped to next frame.");
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressPositionSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.5)
        {
            return;
        }

        FireAndForget(_shellPlaybackCommands.SeekAsync(TimeSpan.FromSeconds(e.NewValue)));
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
        FireAndForget(_shellPlaybackCommands.SeekAsync(TimeSpan.FromSeconds(e.NewValue)));
        UpdateScrubTimeLabels(e.NewValue, FullscreenPositionSlider.Maximum);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolumeSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.001)
        {
            return;
        }

        var normalizedVolume = Math.Clamp(e.NewValue / 100d, 0, 1);
        ViewModel.Transport.Volume = normalizedVolume;
        FireAndForget(_shellPlaybackCommands.ApplyAudioPreferencesAsync(normalizedVolume, MuteToggleButton?.IsChecked == true));
        if (!_isInitializingShellState)
        {
            PersistAudioStatePreferences();
        }
    }

    private void MuteToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var isMuted = MuteToggleButton.IsChecked == true;
        ViewModel.Transport.IsMuted = isMuted;
        var normalizedVolume = Math.Clamp((VolumeSlider?.Value ?? (ViewModel.Transport.Volume * 100d)) / 100d, 0, 1);
        FireAndForget(_shellPlaybackCommands.ApplyAudioPreferencesAsync(normalizedVolume, isMuted));
        UpdateMuteButtonVisual();
        if (!_isInitializingShellState)
        {
            PersistAudioStatePreferences();
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

    private void PlaybackSpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressPlaybackSpeedSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.01)
        {
            return;
        }

        SetPlaybackRate(e.NewValue);
    }

    private void SetPlaybackRate(double speed, bool persistSettings = true, bool showStatus = true)
    {
        var clamped = Math.Clamp(speed, 0.25, 2.0);
        ViewModel.Transport.PlaybackRate = clamped;
        FireAndForget(_shellPlaybackCommands.SetPlaybackRateAsync(clamped));
        UpdatePlaybackRateFlyoutChecks();
        SyncPlaybackSpeedSlider(clamped);
        if (persistSettings && !_isInitializingShellState)
        {
            PersistPlaybackDefaults();
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
        var applied = await _subtitleWorkflowService.SelectTranscriptionModelAsync(selection.Key);
        if (!applied)
        {
            ApplyWorkflowSnapshot(_subtitleWorkflowService.Current);
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

        var applied = await _subtitleWorkflowService.SelectTranslationModelAsync(selection.Key);
        if (!applied)
        {
            ApplyWorkflowSnapshot(_subtitleWorkflowService.Current);
        }
    }

    private async void TranslationToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || TranslationToggleSwitch is null)
        {
            return;
        }

        await _subtitleWorkflowService.SetTranslationEnabledAsync(TranslationToggleSwitch.IsOn);
    }

    private async void AutoTranslateToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || AutoTranslateToggleSwitch is null)
        {
            return;
        }

        await _subtitleWorkflowService.SetAutoTranslateEnabledAsync(AutoTranslateToggleSwitch.IsOn);
    }

    private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            e.Handled = true;
            FireAndForget(ExitFullscreenAsync());
            return;
        }

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

        FireAndForget(ExecuteShortcutCommandAsync(commandId));
        return true;
    }

    private async Task ExecuteShortcutCommandAsync(string commandId)
    {
        var result = await _shortcutCommandExecutor.ExecuteAsync(commandId);
        if (result.RequiresOverlayInteraction)
        {
            RegisterFullscreenOverlayInteraction();
        }

        if (ShortcutUpdatesPreferences(commandId))
        {
            ApplyPreferencesSnapshot(_shellPreferencesService.Current);
        }

        switch (result.ShellAction)
        {
            case ShortcutShellAction.ToggleFullscreen:
                if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
                {
                    await ExitFullscreenAsync();
                }
                else
                {
                    await EnterFullscreenAsync();
                }
                break;

            case ShortcutShellAction.TogglePictureInPicture:
                await SetWindowModeAsync(_windowModeService.CurrentMode == PlaybackWindowMode.PictureInPicture
                    ? PlaybackWindowMode.Standard
                    : PlaybackWindowMode.PictureInPicture);
                break;

            case ShortcutShellAction.ToggleSubtitleVisibility:
                ToggleSubtitleVisibility();
                break;
        }

        if (result.ItemToLoad is not null)
        {
            await LoadPlaybackItemAsync(result.ItemToLoad);
        }

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ShowStatus(result.StatusMessage, result.IsError);
        }
    }

    private static bool ShortcutUpdatesPreferences(string commandId)
    {
        return commandId is "mute"
            or "subtitle_delay_back"
            or "subtitle_delay_forward"
            or "audio_delay_back"
            or "audio_delay_forward"
            or "speed_up"
            or "speed_down"
            or "speed_reset";
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
        foreach (var binding in _shortcutProfileService.Current.NormalizedBindings)
        {
            if (!TryResolveShortcutBinding(binding.CommandId, binding.NormalizedGesture, out var resolved))
            {
                continue;
            }

            _resolvedShortcutBindings[binding.CommandId] = resolved;
        }
    }

    private bool TryResolveShortcutBinding(string commandId, string gestureText, out ResolvedShortcutBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(gestureText))
        {
            return false;
        }

        var tokens = gestureText
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var keyToken = tokens[^1];
        if (!TryParseShortcutKey(keyToken, out var key))
        {
            return false;
        }

        var ctrl = false;
        var alt = false;
        var shift = false;
        foreach (var modifier in tokens[..^1])
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
                case StorageFile file when _shellLibraryService.IsSupportedMediaPath(file.Path):
                    files.Add(file.Path);
                    break;
                case StorageFolder folder:
                    folders.Add(folder.Path);
                    break;
            }
        }

        if (IsPlaylistDropTarget(sender))
        {
            var result = _queueCommands.AddDroppedItemsToQueue(files, folders);
            if (!string.IsNullOrWhiteSpace(result.StatusMessage))
            {
                ShowStatus(result.StatusMessage, result.IsError);
            }

            return;
        }

        await ApplyQueueMutationAsync(_queueCommands.EnqueueDroppedItems(files, folders));
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

        UpdateLanguageToolsResponsiveLayout();
        PlayerHost.RequestHostBoundsSync();
        UpdateSubtitleVisibility();
        _stageCoordinator.HandleStageLayoutChanged();
    }

    private void LibraryTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not LibraryEntrySnapshot node || !node.IsFolder)
        {
            return;
        }
        _shellLibraryService.SetExpanded(node.Path, isExpanded: true);
    }

    private async void LibraryTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not LibraryEntrySnapshot node)
        {
            return;
        }

        _selectedLibraryNode = node;
        if (node.IsFolder)
        {
            if (sender.SelectedNode is not null)
            {
                _shellLibraryService.SetExpanded(node.Path, !sender.SelectedNode.IsExpanded);
            }

            UpdateWindowHeader();
            return;
        }

        await LoadLibraryNodeAsync(node);
    }

    private async void LibraryTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is not LibraryEntrySnapshot node)
        {
            return;
        }

        _selectedLibraryNode = node;
        UpdateWindowHeader();
        if (node.IsFolder)
        {
            return;
        }
        if (_isLibraryDragOperationInProgress)
        {
            return;
        }

        await LoadLibraryNodeAsync(node);
    }

    private void LibraryTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        var paths = args.Items
            .OfType<LibraryEntrySnapshot>()
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

    private async Task LoadLibraryNodeAsync(LibraryEntrySnapshot node)
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
            await ApplyQueueMutationAsync(_queueCommands.PlayNow(node.Path));
        }
        finally
        {
            _pendingLibraryLoadPath = null;
        }
    }

    private void PlayerHost_MediaOpened(PlaybackStateSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            _logger.LogInfo(
                "Player host media opened.",
                BabelLogContext.Create(
                    ("path", snapshot.Path),
                    ("duration", snapshot.Duration),
                    ("videoWidth", snapshot.VideoWidth),
                    ("videoHeight", snapshot.VideoHeight),
                    ("displayWidth", snapshot.VideoDisplayWidth),
                    ("displayHeight", snapshot.VideoDisplayHeight)));
            PlayerHost.RequestHostBoundsSync();
            await _shellPlaybackCommands.SetAudioDelayAsync(ViewModel.Settings.AudioDelaySeconds);
            await _shellPlaybackCommands.SetSubtitleDelayAsync(ViewModel.Settings.SubtitleDelaySeconds);
            await _shellPlaybackCommands.SetAspectRatioAsync(ViewModel.Settings.AspectRatio);
            UpdateWindowHeader();
            var result = await _shellPlaybackCommands.HandleMediaOpenedAsync(
                snapshot,
                ViewModel.Settings.ResumeEnabled);
            ShowStatus(result.ResumePosition is TimeSpan
                ? $"Resumed: {Path.GetFileName(snapshot.Path)}"
                : result.StatusMessage);
        });
    }

    private void PlayerHost_MediaEnded(PlaybackStateSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            _logger.LogInfo("Player host media ended.", BabelLogContext.Create(("path", snapshot.Path)));
            var result = _shellPlaybackCommands.HandleMediaEnded(ViewModel.Settings.ResumeEnabled);
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
            _logger.LogError("Player host media failed.", null, BabelLogContext.Create(("message", message), ("path", _shellPlaybackCommands.CurrentPlaybackSnapshot.Path)));
            ShowStatus(message, true);
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

    private void SubtitleWorkflow_StatusChanged(string message)
    {
        DispatcherQueue.TryEnqueue(() => ShowStatus(message));
    }

    private void SubtitleWorkflow_SnapshotChanged(SubtitleWorkflowSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            UpdateWorkflowDiagnostics(snapshot);
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
        _currentShellProjection = projection;
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
        _suppressVolumeSliderChanges = true;
        VolumeSlider.Value = ViewModel.Transport.Volume * 100d;
        _suppressVolumeSliderChanges = false;
        MuteToggleButton.IsChecked = ViewModel.Transport.IsMuted;
        UpdateMuteButtonVisual();
        UpdatePlaybackDiagnostics(projection.Transport);
        UpdatePortraitVideoLanguageToolsState();
        TryApplyStandardAutoFit();
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
            ViewModel.SelectedTranscriptionLabel = snapshot.SelectedTranscriptionLabel;
            ViewModel.SelectedTranslationLabel = snapshot.SelectedTranslationLabel;
            ViewModel.IsTranslationEnabled = snapshot.IsTranslationEnabled;
            ViewModel.IsAutoTranslateEnabled = snapshot.AutoTranslateEnabled;
            ViewModel.SubtitleSource = snapshot.SubtitleSource;
            ViewModel.IsCaptionGenerationInProgress = snapshot.IsCaptionGenerationInProgress;
            ViewModel.SubtitleOverlay.SelectedTranscriptionLabel = snapshot.SelectedTranscriptionLabel;
            ViewModel.SubtitleOverlay.SelectedTranslationLabel = snapshot.SelectedTranslationLabel;

            if (TranscriptionModelComboBox is not null)
            {
                TranscriptionModelComboBox.ItemsSource = snapshot.AvailableTranscriptionModels;
                TranscriptionModelComboBox.SelectedItem = snapshot.AvailableTranscriptionModels
                    .FirstOrDefault(item => string.Equals(item.Key, snapshot.SelectedTranscriptionModelKey, StringComparison.Ordinal));
            }

            if (TranslationModelComboBox is not null)
            {
                TranslationModelComboBox.ItemsSource = snapshot.AvailableTranslationModels;
                TranslationModelComboBox.SelectedItem = snapshot.AvailableTranslationModels
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

    private void UpdateAspectRatioFlyoutChecks()
    {
        UpdateAspectRatioFlyoutChecks(AspectRatioFlyoutSubItem);
    }

    private void UpdateAspectRatioFlyoutChecks(MenuFlyoutSubItem? aspectRatioFlyoutSubItem)
    {
        ApplyToggleFlyoutChecks(
            aspectRatioFlyoutSubItem,
            item => string.Equals(item.Tag as string, ViewModel.Settings.AspectRatio, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateSubtitleRenderModeFlyoutChecks(SubtitleWorkflowSnapshot? snapshot = null)
    {
        var checkedMode = GetEffectiveSubtitleRenderMode();
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
        UpdatePlaybackRateFlyoutChecks(PlaybackRateFlyoutSubItem);
    }

    private void UpdatePlaybackRateFlyoutChecks(MenuFlyoutSubItem? playbackRateFlyoutSubItem)
    {
        ApplyToggleFlyoutChecks(
            playbackRateFlyoutSubItem,
            item => item.Tag is double rate && Math.Abs(rate - ViewModel.Transport.PlaybackRate) < 0.001);

        if (playbackRateFlyoutSubItem is not null)
        {
            playbackRateFlyoutSubItem.Text = $"Playback Rate ({ViewModel.Transport.PlaybackRate:0.00}x)";
        }
    }

    private void UpdateHardwareDecodingFlyoutChecks()
    {
        UpdateHardwareDecodingFlyoutChecks(HardwareDecodingFlyoutSubItem);
    }

    private void UpdateHardwareDecodingFlyoutChecks(MenuFlyoutSubItem? hardwareDecodingFlyoutSubItem)
    {
        ApplyToggleFlyoutChecks(
            hardwareDecodingFlyoutSubItem,
            item => item.Tag is HardwareDecodingMode mode && mode == ViewModel.Settings.HardwareDecodingMode);

        if (hardwareDecodingFlyoutSubItem is not null)
        {
            hardwareDecodingFlyoutSubItem.Text = $"Hardware Decode ({FormatHardwareDecodingLabel(ViewModel.Settings.HardwareDecodingMode)})";
        }
    }

    private static void ApplyToggleFlyoutChecks(MenuFlyoutSubItem? flyoutSubItem, Func<ToggleMenuFlyoutItem, bool> checkSelector)
    {
        if (flyoutSubItem is null)
        {
            return;
        }

        foreach (var item in flyoutSubItem.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = checkSelector(item);
        }
    }

    private void UpdateDelayFlyoutLabels()
    {
        UpdateDelayFlyoutLabels(SubtitleDelayFlyoutSubItem, AudioDelayFlyoutSubItem);
    }

    private void UpdateDelayFlyoutLabels(MenuFlyoutSubItem? subtitleDelayFlyoutSubItem, MenuFlyoutSubItem? audioDelayFlyoutSubItem)
    {
        if (subtitleDelayFlyoutSubItem is not null)
        {
            subtitleDelayFlyoutSubItem.Text = $"Subtitle Delay ({ViewModel.Settings.SubtitleDelaySeconds:+0.00;-0.00;0.00}s)";
        }

        if (audioDelayFlyoutSubItem is not null)
        {
            audioDelayFlyoutSubItem.Text = $"Audio Delay ({ViewModel.Settings.AudioDelaySeconds:+0.00;-0.00;0.00}s)";
        }
    }

    private void RebuildAudioTrackFlyout()
    {
        if (AudioTracksFlyoutSubItem is null)
        {
            return;
        }

        PopulateAudioTrackFlyout(AudioTracksFlyoutSubItem);
    }

    private void PopulateAudioTrackFlyout(MenuFlyoutSubItem audioTracksFlyoutSubItem)
    {
        audioTracksFlyoutSubItem.Items.Clear();
        var audioTracks = _currentTracks
            .Where(track => track.Kind == MediaTrackKind.Audio)
            .OrderBy(track => track.Id)
            .ToList();
        if (audioTracks.Count == 0)
        {
            audioTracksFlyoutSubItem.Items.Add(new MenuFlyoutItem
            {
                Text = "No alternate tracks",
                IsEnabled = false
            });
            return;
        }

        foreach (var track in audioTracks)
        {
            audioTracksFlyoutSubItem.Items.Add(CreateTrackFlyoutItem(track, AudioTrackFlyoutItem_Click));
        }
    }

    private void RebuildEmbeddedSubtitleTrackFlyout()
    {
        if (EmbeddedSubtitleTracksFlyoutSubItem is null)
        {
            return;
        }

        PopulateEmbeddedSubtitleTrackFlyout(EmbeddedSubtitleTracksFlyoutSubItem);
    }

    private void PopulateEmbeddedSubtitleTrackFlyout(MenuFlyoutSubItem embeddedSubtitleTracksFlyoutSubItem)
    {
        embeddedSubtitleTracksFlyoutSubItem.Items.Clear();
        var hasSelectedEmbeddedTrack = _currentTracks.Any(track => track.Kind == MediaTrackKind.Subtitle && track.IsSelected);
        var offItem = new ToggleMenuFlyoutItem
        {
            Text = "Off",
            Tag = "off",
            IsChecked = !hasSelectedEmbeddedTrack
        };
        offItem.Click += EmbeddedSubtitleTrackFlyoutItem_Click;
        embeddedSubtitleTracksFlyoutSubItem.Items.Add(offItem);

        var subtitleTracks = _currentTracks
            .Where(track => track.Kind == MediaTrackKind.Subtitle)
            .OrderBy(track => track.Id)
            .ToList();
        if (subtitleTracks.Count == 0)
        {
            embeddedSubtitleTracksFlyoutSubItem.Items.Add(new MenuFlyoutSeparator());
            embeddedSubtitleTracksFlyoutSubItem.Items.Add(new MenuFlyoutItem
            {
                Text = "No embedded subtitle tracks",
                IsEnabled = false
            });
            return;
        }

        embeddedSubtitleTracksFlyoutSubItem.Items.Add(new MenuFlyoutSeparator());
        foreach (var track in subtitleTracks)
        {
            embeddedSubtitleTracksFlyoutSubItem.Items.Add(CreateTrackFlyoutItem(track, EmbeddedSubtitleTrackFlyoutItem_Click));
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
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplySubtitlePresentationChange(
            new ShellSubtitlePresentationChange(
                ViewModel.Settings.SubtitleRenderMode,
                updater(ViewModel.Settings.SubtitleStyle))));

        ApplySubtitleStyleSettings();
        UpdateSubtitleVisibility();
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

        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyPlaybackDefaultsChange(
            new ShellPlaybackDefaultsChange(
                ViewModel.Settings.HardwareDecodingMode,
                ViewModel.Transport.PlaybackRate,
                ViewModel.Settings.AudioDelaySeconds,
                ViewModel.Settings.SubtitleDelaySeconds,
                aspectRatio)));
        FireAndForget(_shellPlaybackCommands.SetAspectRatioAsync(aspectRatio));
        UpdateAspectRatioFlyoutChecks();
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
        var result = _subtitleWorkflowService.SelectRenderMode(mode, ViewModel.Settings.SubtitleRenderMode);
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplySubtitlePresentationChange(
            new ShellSubtitlePresentationChange(result.RequestedRenderMode, ViewModel.Settings.SubtitleStyle)));
        UpdateSubtitleRenderModeFlyoutChecks();
        UpdateSubtitleVisibility();
        UpdateOverlayControlState();
        ShowStatus($"Subtitle mode: {FormatSubtitleRenderModeLabel(result.EffectiveRenderMode)}.");
    }

    private void HardwareDecodingFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: HardwareDecodingMode mode })
        {
            return;
        }

        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyPlaybackDefaultsChange(
            new ShellPlaybackDefaultsChange(
                mode,
                ViewModel.Transport.PlaybackRate,
                ViewModel.Settings.AudioDelaySeconds,
                ViewModel.Settings.SubtitleDelaySeconds,
                ViewModel.Settings.AspectRatio)));
        FireAndForget(_shellPlaybackCommands.SetHardwareDecodingModeAsync(mode));
        UpdateHardwareDecodingFlyoutChecks();
        ShowStatus($"Hardware decode: {FormatHardwareDecodingLabel(mode)}.");
    }

    private void AudioTrackFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: int trackId, Text: string label })
        {
            return;
        }

        FireAndForget(_shellPlaybackCommands.SetAudioTrackAsync(trackId));
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

        MediaTrackInfo? track = null;
        if (item.Tag is int trackId)
        {
            track = _currentTracks.FirstOrDefault(candidate => candidate.Kind == MediaTrackKind.Subtitle && candidate.Id == trackId);
            if (track is null)
            {
                return;
            }
        }
        else if (item.Tag is not string offValue || offValue != "off")
        {
            return;
        }

        var result = await _shellPlaybackCommands.SelectEmbeddedSubtitleTrackAsync(
            _shellPlaybackCommands.CurrentPlaybackSnapshot.Path,
            ViewModel.SubtitleSource,
            track);

        if (result.TrackSelectionChanged)
        {
            ApplyTrackSelection(MediaTrackKind.Subtitle, result.SelectedSubtitleTrackId);
            RebuildEmbeddedSubtitleTrackFlyout();
        }

        ShowStatus(result.StatusMessage, result.IsError);
    }

    private void AdjustSubtitleDelay(double delta)
    {
        var updatedDelay = ViewModel.Settings.SubtitleDelaySeconds + delta;
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyPlaybackDefaultsChange(
            new ShellPlaybackDefaultsChange(
                ViewModel.Settings.HardwareDecodingMode,
                ViewModel.Transport.PlaybackRate,
                ViewModel.Settings.AudioDelaySeconds,
                updatedDelay,
                ViewModel.Settings.AspectRatio)));
        FireAndForget(_shellPlaybackCommands.SetSubtitleDelayAsync(updatedDelay));
        UpdateDelayFlyoutLabels();
        SyncSubtitleDelayValueText();
        ShowStatus($"Subtitle delay: {updatedDelay:+0.00;-0.00;0.00}s");
    }

    private void ResetSubtitleDelay()
    {
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyPlaybackDefaultsChange(
            new ShellPlaybackDefaultsChange(
                ViewModel.Settings.HardwareDecodingMode,
                ViewModel.Transport.PlaybackRate,
                ViewModel.Settings.AudioDelaySeconds,
                0,
                ViewModel.Settings.AspectRatio)));
        FireAndForget(_shellPlaybackCommands.SetSubtitleDelayAsync(0));
        UpdateDelayFlyoutLabels();
        SyncSubtitleDelayValueText();
        ShowStatus("Subtitle delay reset.");
    }

    private void AdjustAudioDelay(double delta)
    {
        var updatedDelay = ViewModel.Settings.AudioDelaySeconds + delta;
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyPlaybackDefaultsChange(
            new ShellPlaybackDefaultsChange(
                ViewModel.Settings.HardwareDecodingMode,
                ViewModel.Transport.PlaybackRate,
                updatedDelay,
                ViewModel.Settings.SubtitleDelaySeconds,
                ViewModel.Settings.AspectRatio)));
        FireAndForget(_shellPlaybackCommands.SetAudioDelayAsync(updatedDelay));
        UpdateDelayFlyoutLabels();
        ShowStatus($"Audio delay: {updatedDelay:+0.00;-0.00;0.00}s");
    }

    private void ResetAudioDelay()
    {
        ApplyPreferencesSnapshot(_shellPreferencesService.ApplyPlaybackDefaultsChange(
            new ShellPlaybackDefaultsChange(
                ViewModel.Settings.HardwareDecodingMode,
                ViewModel.Transport.PlaybackRate,
                0,
                ViewModel.Settings.SubtitleDelaySeconds,
                ViewModel.Settings.AspectRatio)));
        FireAndForget(_shellPlaybackCommands.SetAudioDelayAsync(0));
        UpdateDelayFlyoutLabels();
        ShowStatus("Audio delay reset.");
    }

    private void SubtitleDelayBack_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(-0.05);

    private void SubtitleDelayForward_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(0.05);

    private void ResetSubtitleDelay_Click(object sender, RoutedEventArgs e) => ResetSubtitleDelay();

    private void SyncSubtitleDelayValueText()
    {
        if (SubtitleDelayValueText is not null)
        {
            SubtitleDelayValueText.Text = $"{ViewModel.Settings.SubtitleDelaySeconds:+0.00;-0.00;0.00}s";
        }
    }

    private void SyncPlaybackSpeedSlider(double speed)
    {
        if (PlaybackSpeedSlider is null)
        {
            return;
        }

        _suppressPlaybackSpeedSliderChanges = true;
        PlaybackSpeedSlider.Value = speed;
        _suppressPlaybackSpeedSliderChanges = false;
        if (PlaybackSpeedValueText is not null)
        {
            PlaybackSpeedValueText.Text = $"{speed:0.00}x";
        }
    }

    private void AudioDelayBack_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(-0.05);

    private void AudioDelayForward_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(0.05);

    private void ResetAudioDelay_Click(object sender, RoutedEventArgs e) => ResetAudioDelay();

    private void ResumePlaybackToggleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item)
        {
            return;
        }

        PersistResumeEnabledPreference(item.IsChecked);
        _shellPlaybackCommands.SetResumeTrackingEnabled(item.IsChecked);
        if (ResumePlaybackToggleItem is not null && !ReferenceEquals(item, ResumePlaybackToggleItem))
        {
            ResumePlaybackToggleItem.IsChecked = item.IsChecked;
        }

        if (!item.IsChecked)
        {
            _shellPlaybackCommands.ClearResumeHistory();
        }

        ShowStatus(item.IsChecked ? "Resume playback enabled." : "Resume playback disabled.");
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
        var updatedProfile = await _credentialDialogService.EditShortcutsAsync(_shortcutProfileService.Current.Profile);
        if (updatedProfile is null)
        {
            return;
        }

        PersistShortcutProfile(updatedProfile);
        RebuildShortcutBindings();
        ShowStatus("Keyboard shortcuts updated.");
    }

    private async void ExportCurrentSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (!_subtitleWorkflowService.HasCurrentCues)
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

        _subtitleWorkflowService.ExportCurrentSubtitles(exportPath);
        ShowStatus($"Exported subtitles: {Path.GetFileName(exportPath)}");
    }

    private async Task QueueFolderIntoPlaylistAsync(bool autoplay)
    {
        var folder = await _filePickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await ApplyQueueMutationAsync(_queueCommands.EnqueueFolder(folder, autoplay));
    }

    private async Task ApplyQueueMutationAsync(ShellQueueMediaResult result)
    {
        foreach (var folder in result.PinnedFolders)
        {
            var libraryResult = _shellLibraryService.PinRoot(folder);
            if (!libraryResult.IsError)
            {
                ViewModel.Browser.IsVisible = true;
                BrowserPaneToggle.IsChecked = true;
                PersistLayoutPreferences();
            }
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
            AspectRatio = ViewModel.Settings.AspectRatio,
            AudioDelaySeconds = ViewModel.Settings.AudioDelaySeconds,
            SubtitleDelaySeconds = ViewModel.Settings.SubtitleDelaySeconds,
            Volume = ViewModel.Transport.Volume,
            IsMuted = ViewModel.Transport.IsMuted,
            ResumeEnabled = ViewModel.Settings.ResumeEnabled,
            PreviousPlaybackState = _shellPlaybackCommands.CurrentPlaybackSnapshot
        };
    }

    private async Task LoadPlaybackItemAsync(PlaylistItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            return;
        }

        _isLanguageToolsAutoCollapseOverridden = false;
        _pendingAutoFitPath = item.Path;
        _lastAutoFitSignature = null;
        try
        {
            var loaded = await _shellPlaybackCommands.LoadPlaybackItemAsync(
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
        catch (Exception ex)
        {
            _logger.LogError("Window-level media load failed.", ex, BabelLogContext.Create(("path", item.Path), ("displayName", item.DisplayName)));
            ShowStatus(ex.Message, true);
        }
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
        UpdateQueueDiagnostics(snapshot);
    }

    private void RebuildLibraryTree()
    {
        LibraryTree.RootNodes.Clear();
        foreach (var root in ViewModel.Browser.Roots)
        {
            LibraryTree.RootNodes.Add(CreateTreeNode(root));
        }
    }

    private TreeViewNode CreateTreeNode(LibraryEntrySnapshot model)
    {
        var node = new TreeViewNode
        {
            Content = model,
            IsExpanded = model.IsExpanded,
            HasUnrealizedChildren = model.HasUnrealizedChildren
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

    private void UpdateSubtitleOverlay(SubtitleWorkflowSnapshot snapshot)
    {
        var presentation = _subtitleWorkflowService.GetOverlayPresentation(
            ViewModel.Settings.SubtitleRenderMode,
            subtitlesVisible: ViewModel.Settings.SubtitleRenderMode != SubtitleRenderMode.Off);
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
            !string.IsNullOrWhiteSpace(_currentShellProjection.Transport.Path));
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
            FireAndForget(_shellPlaybackCommands.SeekAsync(TimeSpan.FromSeconds(slider.Value)));
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

        var result = _queueCommands.AddToQueue(paths);
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
        var result = await _shellPlaybackCommands.PrepareForTranscriptionRefreshAsync(
            _subtitleWorkflowService.Current,
            _shellPlaybackCommands.CurrentPlaybackSnapshot,
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
        if (GetEffectiveSubtitleRenderMode() == SubtitleRenderMode.Off)
        {
            return;
        }

        var result = await _shellPlaybackCommands.EvaluateCaptionStartupGateAsync(
            snapshot,
            _shellPlaybackCommands.CurrentPlaybackSnapshot,
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

    private static string FormatSubtitleRenderModeButtonLabel(SubtitleRenderMode mode)
    {
        return mode switch
        {
            SubtitleRenderMode.Off => "Off",
            SubtitleRenderMode.SourceOnly => "Source",
            SubtitleRenderMode.TranslationOnly => "Translation",
            SubtitleRenderMode.Dual => "Dual",
            _ => "Translation"
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
