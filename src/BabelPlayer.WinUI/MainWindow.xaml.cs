using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;
using Windows.System;
using Windows.UI;

namespace BabelPlayer.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly SettingsFacade _settingsFacade = new();
    private readonly LibraryBrowserService _libraryBrowserService = new();
    private readonly PlaylistController _playlistController = new();
    private readonly PlaybackSessionController _playbackSessionController;
    private readonly CredentialFacade _credentialFacade = new();
    private readonly SubtitleWorkflowController _subtitleWorkflowController;
    private readonly ShortcutService _shortcutService = new();
    private readonly DispatcherTimer _transportTimer;
    private readonly DispatcherTimer _fullscreenControlsTimer;
    private readonly DispatcherTimer _resumeTimer;
    private readonly IFilePickerService _filePickerService;
    private readonly WinUIWindowModeService _windowModeService;
    private readonly WinUICredentialDialogService _credentialDialogService;
    private readonly IRuntimeBootstrapService _runtimeBootstrapService;
    private readonly List<PlaybackResumeEntry> _resumeEntries = [];
    private readonly List<MediaTrackInfo> _currentTracks = [];
    private FullscreenOverlayWindow? _fullscreenOverlayWindow;
    private SubtitleOverlayWindow? _subtitleOverlayWindow;
    private bool _suppressPositionSliderChanges;
    private bool _suppressFullscreenSliderChanges;
    private bool _suppressWorkflowControlEvents;
    private bool _suppressWindowModeButtonChanges;
    private bool _isPositionScrubbing;
    private bool _isInitializingShellState;
    private bool _isNormalizingWinUITranscriptionSelection;
    private bool _isFullscreenOverlayVisible;
    private bool _isWindowActive = true;
    private bool _isFullscreenOverlayInteracting;
    private bool _hasAttemptedSystemBackdrop;
    private int _modalUiSuppressionCount;
    private Slider? _activeScrubber;
    private long _lastFullscreenInputTick;
    private long _lastOverlayTimerResetTick;
    private long _fullscreenOverlayHideBlockedUntilTick;
    private string? _pendingAutoFitPath;
    private string? _lastAutoFitSignature;
    private string? _subtitleSourceOnlyOverrideVideoPath;
    private bool _autoResumePlaybackAfterCaptionReady;
    private string? _autoResumePlaybackPath;
    private TimeSpan _autoResumePlaybackPosition = TimeSpan.Zero;
    private bool _autoResumePlaybackFromBeginning = true;
    private SubtitleRenderMode _lastNonOffSubtitleRenderMode = SubtitleRenderMode.TranslationOnly;
    private string _selectedAspectRatio = "auto";
    private double _audioDelaySeconds;
    private double _subtitleDelaySeconds;
    private MicaBackdrop? _micaBackdrop;
    private readonly Dictionary<string, ResolvedShortcutBinding> _resolvedShortcutBindings = new(StringComparer.OrdinalIgnoreCase);
    private Border AppTitleBar = null!;
    private TextBlock WindowTitleTextBlock = null!;
    private ToggleButton ThemeToggleButton = null!;
    private CommandBar ShellCommandBar = null!;
    private DropDownButton PlaybackOptionsButton = null!;
    private MenuFlyout PlaybackOptionsFlyout = null!;
    private AppBarToggleButton SubtitleVisibilityToggleButton = null!;
    private AppBarToggleButton BrowserPaneToggle = null!;
    private AppBarToggleButton PlaylistPaneToggle = null!;
    private AppBarToggleButton ImmersiveToggleButton = null!;
    private AppBarToggleButton FullscreenToggleButton = null!;
    private AppBarToggleButton PictureInPictureToggleButton = null!;
    private InfoBar StatusInfoBar = null!;
    private Grid ShellContentGrid = null!;
    private Border BrowserPane = null!;
    private Border PlaylistPane = null!;
    private Border PlayerPane = null!;
    private Border TransportPane = null!;
    private Border DecoderBadge = null!;
    private Border SubtitleOverlayBorder = null!;
    private ColumnDefinition BrowserColumn = null!;
    private ColumnDefinition PlaylistColumn = null!;
    private TreeView LibraryTree = null!;
    private MpvHostControl PlayerHost = null!;
    private TextBlock HardwareDecoderTextBlock = null!;
    private TextBlock SourceSubtitleTextBlock = null!;
    private TextBlock TranslatedSubtitleTextBlock = null!;
    private TextBlock PlaylistSummaryTextBlock = null!;
    private ListView PlaylistList = null!;
    private Button PlayPauseButton = null!;
    private Slider PositionSlider = null!;
    private TextBlock TimeTextBlock = null!;
    private Slider VolumeSlider = null!;
    private ToggleButton MuteToggleButton = null!;
    private ComboBox SpeedComboBox = null!;
    private ComboBox TranscriptionModelComboBox = null!;
    private ComboBox TranslationModelComboBox = null!;
    private ToggleSwitch TranslationToggleSwitch = null!;
    private ToggleSwitch AutoTranslateToggleSwitch = null!;
    private MenuFlyoutSubItem SubtitleRenderModeFlyoutSubItem = null!;
    private MenuFlyoutSubItem SubtitleStyleFlyoutSubItem = null!;
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

    public MainWindow()
    {
        // Removed: InitializeComponent(); (not needed for code-built UI in WinUI 3)

        RootGrid = new Grid();
        Content = RootGrid;
        _playbackSessionController = new PlaybackSessionController(_playlistController);
        _filePickerService = new WinUIFilePickerService(this);
        _windowModeService = new WinUIWindowModeService(this);
        _windowModeService.SetWindowIcon(Path.Combine(AppContext.BaseDirectory, "BabelPlayer.ico"));
        _runtimeBootstrapService = new RuntimeBootstrapService();
        _transportTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _fullscreenControlsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _resumeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _credentialDialogService = new WinUICredentialDialogService(RootGrid, SuppressDialogPresentation);
        _subtitleWorkflowController = new SubtitleWorkflowController(
            _credentialFacade,
            _credentialDialogService,
            _filePickerService,
            _runtimeBootstrapService);
        BuildShell();

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
        PlaylistList.ItemsSource = ViewModel.Playlist.Items;

        _transportTimer.Tick += TransportTimer_Tick;
        _fullscreenControlsTimer.Tick += FullscreenControlsTimer_Tick;
        _resumeTimer.Tick += ResumeTimer_Tick;
        _transportTimer.Start();

        PlayerHost.MediaOpened += PlayerHost_MediaOpened;
        PlayerHost.MediaEnded += PlayerHost_MediaEnded;
        PlayerHost.MediaFailed += PlayerHost_MediaFailed;
        PlayerHost.TracksChanged += PlayerHost_TracksChanged;
        PlayerHost.RuntimeInstallProgress += PlayerHost_RuntimeInstallProgress;
        PlayerHost.PlaybackStateChanged += PlayerHost_PlaybackStateChanged;
        PlayerHost.InputActivity += PlayerHost_InputActivity;
        PlayerHost.FullscreenExitRequested += PlayerHost_FullscreenExitRequested;
        PlayerHost.ShortcutKeyPressed += PlayerHost_ShortcutKeyPressed;
        _subtitleWorkflowController.StatusChanged += SubtitleWorkflowController_StatusChanged;
        _subtitleWorkflowController.SnapshotChanged += SubtitleWorkflowController_SnapshotChanged;

        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;

        InitializeShellState();
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
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var panelBrush = new SolidColorBrush(ColorHelper.FromArgb(180, 26, 32, 42));
        var borderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 54, 69, 88));
        var accentBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 47, 111, 178));

        AppTitleBar = new Border
        {
            Padding = new Thickness(18, 14, 18, 14),
            Background = panelBrush
        };
        Grid.SetRow(AppTitleBar, 0);

        var titleBarGrid = new Grid
        {
            ColumnSpacing = 12
        };
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = accentBrush,
            Child = new TextBlock
            {
                Text = "B",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            }
        };
        titleBarGrid.Children.Add(badge);

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
        titleStack.Children.Add(new TextBlock
        {
            Text = "WinUI 3 migration shell",
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        });
        titleBarGrid.Children.Add(titleStack);

        var titleButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        Grid.SetColumn(titleButtons, 2);
        ThemeToggleButton = new ToggleButton
        {
            Content = new TextBlock { Text = "Dark" }
        };
        ThemeToggleButton.Checked += ThemeToggleButton_Checked;
        ThemeToggleButton.Unchecked += ThemeToggleButton_Unchecked;
        titleButtons.Children.Add(ThemeToggleButton);
        titleBarGrid.Children.Add(titleButtons);
        AppTitleBar.Child = titleBarGrid;
        RootGrid.Children.Add(AppTitleBar);

        ShellCommandBar = new CommandBar
        {
            Margin = new Thickness(16, 12, 16, 0),
            Background = panelBrush,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right
        };
        Grid.SetRow(ShellCommandBar, 1);
        ShellCommandBar.PrimaryCommands.Add(CreatePrimaryCommand("Open", Symbol.OpenFile, OpenFile_Click));
        ShellCommandBar.PrimaryCommands.Add(CreatePrimaryCommand("Folder", Symbol.Folder, OpenFolder_Click));
        ShellCommandBar.PrimaryCommands.Add(CreatePrimaryCommand("Import Subs", Symbol.Edit, ImportSubtitle_Click));
        SubtitleVisibilityToggleButton = new AppBarToggleButton
        {
            Label = "Subtitles"
        };
        SubtitleVisibilityToggleButton.Click += SubtitleVisibilityToggleButton_Click;
        ShellCommandBar.PrimaryCommands.Add(SubtitleVisibilityToggleButton);
        ImmersiveToggleButton = new AppBarToggleButton
        {
            Label = "Immersive",
            Icon = new SymbolIcon(Symbol.HideBcc)
        };
        ImmersiveToggleButton.Click += ImmersiveToggleButton_Click;
        ShellCommandBar.PrimaryCommands.Add(ImmersiveToggleButton);
        FullscreenToggleButton = new AppBarToggleButton
        {
            Label = "Fullscreen",
            Icon = new SymbolIcon(Symbol.FullScreen)
        };
        FullscreenToggleButton.Click += FullscreenToggleButton_Click;
        ShellCommandBar.PrimaryCommands.Add(FullscreenToggleButton);
        PictureInPictureToggleButton = new AppBarToggleButton
        {
            Label = "PiP",
            Icon = new SymbolIcon(Symbol.SwitchApps)
        };
        PictureInPictureToggleButton.Click += PictureInPictureToggleButton_Click;
        ShellCommandBar.PrimaryCommands.Add(PictureInPictureToggleButton);
        PlaybackOptionsFlyout = BuildPlaybackOptionsFlyout();
        PlaybackOptionsButton = new DropDownButton
        {
            Content = "Playback",
            Flyout = PlaybackOptionsFlyout,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        BrowserPaneToggle = new AppBarToggleButton { Label = "Browser Pane" };
        BrowserPaneToggle.Click += BrowserPaneToggle_Click;
        PlaylistPaneToggle = new AppBarToggleButton { Label = "Playlist Pane" };
        PlaylistPaneToggle.Click += PlaylistPaneToggle_Click;
        ShellCommandBar.SecondaryCommands.Add(BrowserPaneToggle);
        ShellCommandBar.SecondaryCommands.Add(PlaylistPaneToggle);
        ShellCommandBar.SecondaryCommands.Add(new AppBarButton { Label = "Add Videos Root" });
        ((AppBarButton)ShellCommandBar.SecondaryCommands[^1]).Click += AddRootFolder_Click;
        RootGrid.Children.Add(ShellCommandBar);
        titleButtons.Children.Insert(0, PlaybackOptionsButton);

        StatusInfoBar = new InfoBar
        {
            Margin = new Thickness(16, 12, 16, 0),
            IsClosable = true,
            IsOpen = true,
            Message = "Open local media to begin.",
            Severity = InfoBarSeverity.Informational
        };
        Grid.SetRow(StatusInfoBar, 2);
        RootGrid.Children.Add(StatusInfoBar);

        ShellContentGrid = new Grid
        {
            Padding = new Thickness(16, 12, 16, 12),
            ColumnSpacing = 16
        };
        Grid.SetRow(ShellContentGrid, 3);
        BrowserColumn = new ColumnDefinition { Width = new GridLength(280) };
        PlaylistColumn = new ColumnDefinition { Width = new GridLength(320) };
        ShellContentGrid.ColumnDefinitions.Add(BrowserColumn);
        ShellContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ShellContentGrid.ColumnDefinitions.Add(PlaylistColumn);
        RootGrid.Children.Add(ShellContentGrid);

        BrowserPane = CreatePanelBorder(panelBrush, borderBrush);
        Grid.SetColumn(BrowserPane, 0);
        ShellContentGrid.Children.Add(BrowserPane);
        BrowserPane.Child = BuildBrowserPane();

        PlayerPane = CreatePanelBorder(panelBrush, borderBrush);
        PlayerPane.MinHeight = 280;
        Grid.SetColumn(PlayerPane, 1);
        ShellContentGrid.Children.Add(PlayerPane);
        PlayerPane.Child = BuildPlayerPane(accentBrush);

        PlaylistPane = CreatePanelBorder(panelBrush, borderBrush);
        Grid.SetColumn(PlaylistPane, 2);
        ShellContentGrid.Children.Add(PlaylistPane);
        PlaylistPane.Child = BuildPlaylistPane();

        TransportPane = CreatePanelBorder(panelBrush, borderBrush);
        TransportPane.Margin = new Thickness(16, 0, 16, 16);
        TransportPane.Padding = new Thickness(16, 14, 16, 14);
        Grid.SetRow(TransportPane, 4);
        TransportPane.Child = BuildTransportPane();
        RootGrid.Children.Add(TransportPane);
        RootGrid.SizeChanged += RootGrid_SizeChanged;
    }

    private Border CreatePanelBorder(Brush background, Brush borderBrush)
    {
        return new Border
        {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(14)
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
        return button;
    }

    private MenuFlyout BuildPlaybackOptionsFlyout()
    {
        var flyout = new MenuFlyout();

        SubtitleRenderModeFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Subtitle Mode"
        };
        SubtitleRenderModeFlyoutSubItem.Items.Add(CreateSubtitleRenderModeFlyoutItem("Off", SubtitleRenderMode.Off));
        SubtitleRenderModeFlyoutSubItem.Items.Add(CreateSubtitleRenderModeFlyoutItem("Source Only", SubtitleRenderMode.SourceOnly));
        SubtitleRenderModeFlyoutSubItem.Items.Add(CreateSubtitleRenderModeFlyoutItem("Translation Only", SubtitleRenderMode.TranslationOnly));
        SubtitleRenderModeFlyoutSubItem.Items.Add(CreateSubtitleRenderModeFlyoutItem("Dual", SubtitleRenderMode.Dual));
        flyout.Items.Add(SubtitleRenderModeFlyoutSubItem);

        SubtitleStyleFlyoutSubItem = new MenuFlyoutSubItem
        {
            Text = "Subtitle Style"
        };
        SubtitleStyleFlyoutSubItem.Items.Add(CreateFlyoutItem("Larger Text", IncreaseSubtitleFont_Click));
        SubtitleStyleFlyoutSubItem.Items.Add(CreateFlyoutItem("Smaller Text", DecreaseSubtitleFont_Click));
        SubtitleStyleFlyoutSubItem.Items.Add(new MenuFlyoutSeparator());
        SubtitleStyleFlyoutSubItem.Items.Add(CreateFlyoutItem("More Background", IncreaseSubtitleBackground_Click));
        SubtitleStyleFlyoutSubItem.Items.Add(CreateFlyoutItem("Less Background", DecreaseSubtitleBackground_Click));
        SubtitleStyleFlyoutSubItem.Items.Add(new MenuFlyoutSeparator());
        SubtitleStyleFlyoutSubItem.Items.Add(CreateFlyoutItem("Raise Subtitles", RaiseSubtitles_Click));
        SubtitleStyleFlyoutSubItem.Items.Add(CreateFlyoutItem("Lower Subtitles", LowerSubtitles_Click));
        SubtitleStyleFlyoutSubItem.Items.Add(new MenuFlyoutSeparator());
        var translationColorFlyout = new MenuFlyoutSubItem
        {
            Text = "Translation Color"
        };
        translationColorFlyout.Items.Add(CreateTranslationColorFlyoutItem("White", "#FFFFFF"));
        translationColorFlyout.Items.Add(CreateTranslationColorFlyoutItem("Amber", "#FFD580"));
        translationColorFlyout.Items.Add(CreateTranslationColorFlyoutItem("Cyan", "#BFEFFF"));
        SubtitleStyleFlyoutSubItem.Items.Add(translationColorFlyout);
        flyout.Items.Add(SubtitleStyleFlyoutSubItem);

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
        UpdateSubtitleRenderModeFlyoutChecks();
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

    private MenuFlyoutItem CreateSubtitleRenderModeFlyoutItem(string text, SubtitleRenderMode mode)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            Tag = mode
        };
        item.Click += SubtitleRenderModeFlyoutItem_Click;
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
        var headerStack = new StackPanel { Spacing = 2 };
        headerStack.Children.Add(new TextBlock
        {
            Text = "Library",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = "Pinned folders and local media roots",
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        });
        header.Children.Add(headerStack);
        var addButton = new Button { Content = new FontIcon { Glyph = "\uE710" } };
        addButton.Click += AddRootFolder_Click;
        Grid.SetColumn(addButton, 1);
        header.Children.Add(addButton);
        grid.Children.Add(header);

        LibraryTree = new TreeView
        {
            SelectionMode = TreeViewSelectionMode.Single
        };
        LibraryTree.Expanding += LibraryTree_Expanding;
        LibraryTree.ItemInvoked += LibraryTree_ItemInvoked;
        Grid.SetRow(LibraryTree, 1);
        grid.Children.Add(LibraryTree);

        return grid;
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

        PlayerHost = new MpvHostControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        playerStage.Children.Add(PlayerHost);

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
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerStack = new StackPanel { Spacing = 2 };
        headerStack.Children.Add(new TextBlock
        {
            Text = "Playlist",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        PlaylistSummaryTextBlock = new TextBlock
        {
            Text = "No queued items",
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        };
        headerStack.Children.Add(PlaylistSummaryTextBlock);
        header.Children.Add(headerStack);
        var removeButton = new Button { Content = new FontIcon { Glyph = "\uE74D" }, Margin = new Thickness(0, 0, 8, 0) };
        removeButton.Click += RemoveSelected_Click;
        Grid.SetColumn(removeButton, 1);
        header.Children.Add(removeButton);
        var clearButton = new Button { Content = new FontIcon { Glyph = "\uE894" } };
        clearButton.Click += ClearPlaylist_Click;
        Grid.SetColumn(clearButton, 2);
        header.Children.Add(clearButton);
        grid.Children.Add(header);

        PlaylistList = new ListView
        {
            DisplayMemberPath = nameof(PlaylistItem.DisplayName),
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.Single
        };
        PlaylistList.ItemClick += PlaylistList_ItemClick;
        PlaylistList.SelectionChanged += PlaylistList_SelectionChanged;
        Grid.SetRow(PlaylistList, 1);
        grid.Children.Add(PlaylistList);

        return grid;
    }

    private UIElement BuildTransportPane()
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 12
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < 11; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = index switch
                {
                    7 => new GridLength(1, GridUnitType.Star),
                    9 => new GridLength(150),
                    _ => GridLength.Auto
                }
            });
        }

        var previousButton = new Button { Content = new FontIcon { Glyph = "\uE892" } };
        previousButton.Click += PreviousTrack_Click;
        grid.Children.Add(previousButton);

        var backButton = new Button { Content = new FontIcon { Glyph = "\uE896" } };
        backButton.Click += SeekBack_Click;
        Grid.SetColumn(backButton, 1);
        grid.Children.Add(backButton);

        PlayPauseButton = new Button { Content = "Play", MinWidth = 84 };
        PlayPauseButton.Click += PlayPauseButton_Click;
        Grid.SetColumn(PlayPauseButton, 2);
        grid.Children.Add(PlayPauseButton);

        var previousFrameButton = new Button { Content = new FontIcon { Glyph = "\uE100" } };
        previousFrameButton.Click += PreviousFrame_Click;
        Grid.SetColumn(previousFrameButton, 3);
        grid.Children.Add(previousFrameButton);

        var nextFrameButton = new Button { Content = new FontIcon { Glyph = "\uE101" } };
        nextFrameButton.Click += NextFrame_Click;
        Grid.SetColumn(nextFrameButton, 4);
        grid.Children.Add(nextFrameButton);

        var forwardButton = new Button { Content = new FontIcon { Glyph = "\uE893" } };
        forwardButton.Click += SeekForward_Click;
        Grid.SetColumn(forwardButton, 5);
        grid.Children.Add(forwardButton);

        var nextButton = new Button { Content = new FontIcon { Glyph = "\uE893" } };
        nextButton.Click += NextTrack_Click;
        Grid.SetColumn(nextButton, 6);
        grid.Children.Add(nextButton);

        PositionSlider = new Slider { Minimum = 0, Maximum = 1 };
        PositionSlider.ValueChanged += PositionSlider_ValueChanged;
        AttachScrubberHandlers(PositionSlider);
        Grid.SetColumn(PositionSlider, 7);
        grid.Children.Add(PositionSlider);

        TimeTextBlock = new TextBlock { Text = "00:00 / 00:00", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(TimeTextBlock, 8);
        grid.Children.Add(TimeTextBlock);

        VolumeSlider = new Slider { Minimum = 0, Maximum = 1, Value = 0.8 };
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        Grid.SetColumn(VolumeSlider, 9);
        grid.Children.Add(VolumeSlider);

        MuteToggleButton = new ToggleButton { Content = new FontIcon { Glyph = "\uE767" } };
        MuteToggleButton.Click += MuteToggleButton_Click;
        Grid.SetColumn(MuteToggleButton, 10);
        grid.Children.Add(MuteToggleButton);

        SpeedComboBox = new ComboBox { Header = "Speed" };
        SpeedComboBox.Items.Add("0.75x");
        SpeedComboBox.Items.Add("1.00x");
        SpeedComboBox.Items.Add("1.25x");
        SpeedComboBox.Items.Add("1.50x");
        SpeedComboBox.Items.Add("2.00x");
        SpeedComboBox.SelectionChanged += SpeedComboBox_SelectionChanged;
        Grid.SetRow(SpeedComboBox, 1);
        Grid.SetColumn(SpeedComboBox, 0);
        Grid.SetColumnSpan(SpeedComboBox, 2);
        grid.Children.Add(SpeedComboBox);

        TranscriptionModelComboBox = new ComboBox { Header = "Transcription", MinWidth = 220 };
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
        Grid.SetRow(TranscriptionModelComboBox, 1);
        Grid.SetColumn(TranscriptionModelComboBox, 2);
        Grid.SetColumnSpan(TranscriptionModelComboBox, 2);
        grid.Children.Add(TranscriptionModelComboBox);

        TranslationModelComboBox = new ComboBox { Header = "Translation", MinWidth = 220 };
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
        Grid.SetRow(TranslationModelComboBox, 1);
        Grid.SetColumn(TranslationModelComboBox, 4);
        Grid.SetColumnSpan(TranslationModelComboBox, 2);
        grid.Children.Add(TranslationModelComboBox);

        TranslationToggleSwitch = new ToggleSwitch
        {
            Header = "Translate Current Video",
            OffContent = "Off",
            OnContent = "On"
        };
        TranslationToggleSwitch.Toggled += TranslationToggleSwitch_Toggled;
        Grid.SetRow(TranslationToggleSwitch, 2);
        Grid.SetColumn(TranslationToggleSwitch, 0);
        Grid.SetColumnSpan(TranslationToggleSwitch, 2);
        grid.Children.Add(TranslationToggleSwitch);

        AutoTranslateToggleSwitch = new ToggleSwitch
        {
            Header = "Auto Translate Non-English",
            OffContent = "Off",
            OnContent = "On"
        };
        AutoTranslateToggleSwitch.Toggled += AutoTranslateToggleSwitch_Toggled;
        Grid.SetRow(AutoTranslateToggleSwitch, 2);
        Grid.SetColumn(AutoTranslateToggleSwitch, 2);
        Grid.SetColumnSpan(AutoTranslateToggleSwitch, 2);
        grid.Children.Add(AutoTranslateToggleSwitch);

        var hintText = new TextBlock
        {
            Text = "Drag local video files or folders anywhere into the window to queue them.",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        };
        Grid.SetRow(hintText, 2);
        Grid.SetColumn(hintText, 4);
        Grid.SetColumnSpan(hintText, 5);
        grid.Children.Add(hintText);

        return grid;
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

        _resumeEntries.Clear();
        _resumeEntries.AddRange(_settingsFacade.LoadResumeEntries());
        _audioDelaySeconds = settings.AudioDelaySeconds;
        _subtitleDelaySeconds = settings.SubtitleDelaySeconds;
        _selectedAspectRatio = string.IsNullOrWhiteSpace(settings.AspectRatioOverride) ? "auto" : settings.AspectRatioOverride;
        ViewModel.Settings = settings;
        RebuildShortcutBindings();
        ViewModel.WindowTitle = "Babel Player";
        ViewModel.StatusMessage = "Open local media, folders, or subtitle files to start the WinUI migration shell.";
        ViewModel.IsStatusOpen = true;
        ViewModel.ActiveHardwareDecoder = "mpv ready";
        ViewModel.Browser.IsVisible = settings.ShowBrowserPanel;
        ViewModel.Playlist.IsVisible = settings.ShowPlaylistPanel;
        ViewModel.Transport.Volume = Math.Clamp(settings.VolumeLevel, 0, 1);
        ViewModel.Transport.PlaybackRate = settings.DefaultPlaybackRate;
        _lastNonOffSubtitleRenderMode = settings.SubtitleRenderMode == SubtitleRenderMode.Off
            ? SubtitleRenderMode.TranslationOnly
            : settings.SubtitleRenderMode;
        ViewModel.SubtitleOverlay.ShowSource = settings.SubtitleRenderMode is SubtitleRenderMode.SourceOnly or SubtitleRenderMode.Dual;
        ViewModel.SubtitleOverlay.TranslationText = "Drop a file or choose Open to start playback.";
        ViewModel.SelectedTranscriptionLabel = SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny-multilingual").DisplayName;
        ViewModel.SelectedTranslationLabel = SubtitleWorkflowCatalog.GetTranslationModel(null).DisplayName;
        WindowTitleTextBlock.Text = ViewModel.WindowTitle;
        TranslatedSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.TranslationText;
        StatusInfoBar.IsOpen = true;
        StatusInfoBar.Message = ViewModel.StatusMessage;

        ThemeToggleButton.IsChecked = true;
        ApplyTheme(isDark: true);
        VolumeSlider.Value = ViewModel.Transport.Volume;
        MuteToggleButton.IsChecked = settings.IsMuted;
        PlayerHost.Volume = ViewModel.Transport.Volume;
        PlayerHost.SetMute(settings.IsMuted);
        ResumePlaybackToggleItem.IsChecked = settings.ResumeEnabled;
        SpeedComboBox.SelectedItem = $"{settings.DefaultPlaybackRate:0.00}x";
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
        PlaylistPaneToggle.IsChecked = ViewModel.Playlist.IsVisible;
        ApplySubtitleStyleSettings();
        UpdateSubtitleRenderModeFlyoutChecks();
        UpdateHardwareDecodingFlyoutChecks();
        UpdateAspectRatioFlyoutChecks();
        UpdateDelayFlyoutLabels();

        foreach (var root in _libraryBrowserService.BuildPinnedRoots(settings.PinnedRoots))
        {
            ViewModel.Browser.Roots.Add(root);
        }

        RebuildLibraryTree();
        SyncPaneLayout(RootGrid.ActualWidth);
        ApplyAdaptiveStandardLayout(RootGrid.ActualHeight);
        PlayerHost.RequestHostBoundsSync();
        _isInitializingShellState = false;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SystemBackdrop = null;
        _micaBackdrop = null;
        HideFullscreenOverlay();
        _fullscreenOverlayWindow?.CloseOverlay();
        _subtitleOverlayWindow?.CloseOverlay();
        _resumeTimer.Stop();
        SaveCurrentSettings();
    }

    private void SaveCurrentSettings()
    {
        SaveResumePosition();
        var pinnedRoots = ViewModel.Browser.Roots.Select(root => root.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var updatedSettings = _settingsFacade.UpdatePlaybackDefaults(
            _settingsFacade.UpdateLayout(
                ViewModel.Settings,
                ViewModel.Browser.IsVisible,
                ViewModel.Playlist.IsVisible,
                _windowModeService.CurrentMode),
            PlayerHost.HardwareDecodingMode,
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
        if (files.Count == 0)
        {
            return;
        }

        var added = _playlistController.EnqueueFiles(files);
        RefreshPlaylistView();
        await LoadPlaylistItemAsync(added.FirstOrDefault());
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await _filePickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        AddPinnedRoot(folder);
        var files = _libraryBrowserService.EnumerateMediaFiles(folder, recursive: true);
        if (files.Count == 0)
        {
            ShowStatus($"No supported media files were found in {folder}.");
            return;
        }

        var added = _playlistController.EnqueueFolder(folder, files);
        RefreshPlaylistView();
        await LoadPlaylistItemAsync(added.FirstOrDefault());
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
        ViewModel.Playlist.IsVisible = PlaylistPaneToggle.IsChecked == true;
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
            await LoadPlaylistItemAsync(item);
        }
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Playlist.SelectedItem = PlaylistList.SelectedItem as PlaylistItem;
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedIndex < 0)
        {
            return;
        }

        _playlistController.RemoveAt(PlaylistList.SelectedIndex);
        RefreshPlaylistView();
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _playlistController.Clear();
        RefreshPlaylistView();
        ShowStatus("Playlist cleared.");
    }

    private async void PreviousTrack_Click(object sender, RoutedEventArgs e)
    {
        await LoadPlaylistItemAsync(_playlistController.MovePrevious());
    }

    private async void NextTrack_Click(object sender, RoutedEventArgs e)
    {
        await LoadPlaylistItemAsync(_playlistController.MoveNext());
    }

    private void SeekBack_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        PlayerHost.SeekBy(TimeSpan.FromSeconds(-10));
    }

    private void SeekForward_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        PlayerHost.SeekBy(TimeSpan.FromSeconds(10));
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        if (PlayerHost.IsPaused)
        {
            PlayerHost.Play();
            ShowStatus("Playback resumed.");
            return;
        }

        PlayerHost.Pause();
        ShowStatus("Playback paused.");
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        PlayerHost.StepFrame(forward: false);
        ViewModel.Transport.IsPaused = true;
        ShowStatus("Stepped to previous frame.");
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        PlayerHost.StepFrame(forward: true);
        ViewModel.Transport.IsPaused = true;
        ShowStatus("Stepped to next frame.");
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressPositionSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.5)
        {
            return;
        }

        PlayerHost.Position = TimeSpan.FromSeconds(e.NewValue);
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
        PlayerHost.Position = TimeSpan.FromSeconds(e.NewValue);
        UpdateScrubTimeLabels(e.NewValue, FullscreenPositionSlider.Maximum);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ViewModel.Transport.Volume = e.NewValue;
        PlayerHost.Volume = e.NewValue;
        if (!_isInitializingShellState)
        {
            SaveCurrentSettings();
        }
    }

    private void MuteToggleButton_Click(object sender, RoutedEventArgs e)
    {
        PlayerHost.SetMute(MuteToggleButton.IsChecked == true);
        if (!_isInitializingShellState)
        {
            SaveCurrentSettings();
        }
    }

    private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedComboBox.SelectedItem is not string speedLabel || !double.TryParse(speedLabel.Replace("x", string.Empty), out var speed))
        {
            return;
        }

        ViewModel.Transport.PlaybackRate = speed;
        PlayerHost.SetPlaybackRate(speed);
        SaveCurrentSettings();
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
            _autoResumePlaybackAfterCaptionReady = false;
            _autoResumePlaybackPath = null;
            _autoResumePlaybackPosition = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;
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
                PlayerHost.SeekBy(TimeSpan.FromSeconds(-5));
                RegisterFullscreenOverlayInteraction();
                break;
            case "seek_forward_small":
                PlayerHost.SeekBy(TimeSpan.FromSeconds(5));
                RegisterFullscreenOverlayInteraction();
                break;
            case "seek_back_large":
                PlayerHost.SeekBy(TimeSpan.FromSeconds(-15));
                RegisterFullscreenOverlayInteraction();
                break;
            case "seek_forward_large":
                PlayerHost.SeekBy(TimeSpan.FromSeconds(15));
                RegisterFullscreenOverlayInteraction();
                break;
            case "previous_frame":
                PlayerHost.StepFrame(forward: false);
                RegisterFullscreenOverlayInteraction();
                break;
            case "next_frame":
                PlayerHost.StepFrame(forward: true);
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
                await LoadPlaylistItemAsync(_playlistController.MoveNext());
                break;
            case "previous_item":
                await LoadPlaylistItemAsync(_playlistController.MovePrevious());
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
        var clamped = Math.Clamp(speed, 0.25, 2.0);
        ViewModel.Transport.PlaybackRate = clamped;
        PlayerHost.SetPlaybackRate(clamped);
        SpeedComboBox.SelectedItem = $"{clamped:0.00}x";
        SaveCurrentSettings();
        ShowStatus($"Playback speed: {clamped:0.00}x");
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

    private void ThemeToggleButton_Checked(object sender, RoutedEventArgs e) => ApplyTheme(isDark: true);

    private void ThemeToggleButton_Unchecked(object sender, RoutedEventArgs e) => ApplyTheme(isDark: false);

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var storageItems = await e.DataView.GetStorageItemsAsync();
        List<string> files = [];
        foreach (var item in storageItems)
        {
            switch (item)
            {
                case StorageFile file when LibraryBrowserService.IsSupportedMediaFile(file.Path):
                    files.Add(file.Path);
                    break;
                case StorageFolder folder:
                    AddPinnedRoot(folder.Path);
                    files.AddRange(_libraryBrowserService.EnumerateMediaFiles(folder.Path, recursive: true));
                    break;
            }
        }

        if (files.Count == 0)
        {
            ShowStatus("Dropped items did not contain supported media files.");
            return;
        }

        var added = _playlistController.EnqueueFiles(files);
        RefreshPlaylistView();
        await LoadPlaylistItemAsync(added.FirstOrDefault());
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
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
        PlayerHost.RequestHostBoundsSync();
        UpdateSubtitleVisibility();
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen && _isFullscreenOverlayVisible)
        {
            PositionFullscreenOverlay();
        }
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

        if (node.IsFolder)
        {
            AddPinnedRoot(node.Path);
            return;
        }

        var added = _playlistController.EnqueueFiles([node.Path]);
        RefreshPlaylistView();
        await LoadPlaylistItemAsync(added.FirstOrDefault());
    }

    private void TransportTimer_Tick(object? sender, object e)
    {
        var duration = PlayerHost.NaturalDuration.HasTimeSpan ? PlayerHost.NaturalDuration.TimeSpan : TimeSpan.Zero;
        var position = PlayerHost.Position;
        ViewModel.Transport.PositionSeconds = position.TotalSeconds;
        ViewModel.Transport.DurationSeconds = duration.TotalSeconds;
        ViewModel.Transport.CurrentTimeText = position.ToString(@"mm\:ss");
        ViewModel.Transport.DurationText = duration > TimeSpan.Zero ? duration.ToString(@"mm\:ss") : "00:00";
        ViewModel.Transport.IsPaused = PlayerHost.IsPaused;
        ViewModel.Transport.IsMuted = PlayerHost.IsMuted;
        ViewModel.ActiveHardwareDecoder = string.IsNullOrWhiteSpace(PlayerHost.ActiveHardwareDecoder) ? "mpv ready" : PlayerHost.ActiveHardwareDecoder;
        HardwareDecoderTextBlock.Text = ViewModel.ActiveHardwareDecoder;

        UpdatePositionSurfaces(position, duration);

        PlayPauseButton.Content = ViewModel.Transport.IsPaused ? "Play" : "Pause";
        UpdateOverlayControlState();
        TimeTextBlock.Text = $"{ViewModel.Transport.CurrentTimeText} / {ViewModel.Transport.DurationText}";
        MuteToggleButton.IsChecked = ViewModel.Transport.IsMuted;

        _subtitleWorkflowController.UpdatePlaybackPosition(position);
    }

    private void PlayerHost_MediaOpened()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PlayerHost.RequestHostBoundsSync();
            TryApplyStandardAutoFit();
            PlayerHost.SetAudioDelay(_audioDelaySeconds);
            PlayerHost.SetSubtitleDelay(_subtitleDelaySeconds);
            PlayerHost.SetAspectRatio(_selectedAspectRatio);
            var current = _playlistController.CurrentItem;
            ViewModel.WindowTitle = current is null ? "Babel Player" : $"Babel Player - {current.DisplayName}";
            Title = ViewModel.WindowTitle;
            WindowTitleTextBlock.Text = ViewModel.WindowTitle;
            TryApplyResumePosition();
            _resumeTimer.Start();
            ShowStatus(current is null ? "Media opened." : $"Now playing {current.DisplayName}.");
        });
    }

    private void PlayerHost_PlaybackStateChanged(PlaybackStateSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (snapshot.VideoWidth > 0 && snapshot.VideoHeight > 0)
            {
                TryApplyStandardAutoFit();
            }
        });
    }

    private void PlayerHost_MediaEnded()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            _resumeTimer.Stop();
            SaveResumePosition(forceRemoveCompleted: true);
            _autoResumePlaybackAfterCaptionReady = false;
            _autoResumePlaybackPath = null;
            _autoResumePlaybackPosition = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;
            var next = _playlistController.AdvanceAfterMediaEnded();
            RefreshPlaylistView();
            if (next is null)
            {
                ShowStatus("Playback ended.");
                return;
            }

            await LoadPlaylistItemAsync(next);
        });
    }

    private void PlayerHost_MediaFailed(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _resumeTimer.Stop();
            _autoResumePlaybackAfterCaptionReady = false;
            _autoResumePlaybackPath = null;
            _autoResumePlaybackPosition = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;
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
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_isFullscreenOverlayVisible && now - _lastFullscreenInputTick < 80)
        {
            return;
        }

        _lastFullscreenInputTick = now;
        void showOverlay()
        {
            if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
            {
                ShowFullscreenOverlay();
            }
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
        DispatcherQueue.TryEnqueue(() => ApplyWorkflowSnapshot(snapshot));
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
            ViewModel.SubtitleOverlay.IsTranslationEnabled = snapshot.IsTranslationEnabled;
            ViewModel.SubtitleOverlay.IsAutoTranslateEnabled = snapshot.AutoTranslateEnabled;
            ViewModel.SubtitleOverlay.SubtitleSource = snapshot.SubtitleSource;
            ViewModel.SubtitleOverlay.IsCaptionGenerationInProgress = snapshot.IsCaptionGenerationInProgress;
            ViewModel.SubtitleOverlay.StatusText = snapshot.OverlayStatus ?? string.Empty;

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

        UpdateCaptionStartupGate(snapshot);
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

    private void ResumeTimer_Tick(object? sender, object e)
    {
        SaveResumePosition();
    }

    private void SaveResumePosition(bool forceRemoveCompleted = false)
    {
        if (!ViewModel.Settings.ResumeEnabled)
        {
            return;
        }

        var path = PlayerHost.Source?.LocalPath;
        var duration = PlayerHost.NaturalDuration.HasTimeSpan ? PlayerHost.NaturalDuration.TimeSpan : TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(path) || duration <= TimeSpan.FromMinutes(2))
        {
            return;
        }

        var position = PlayerHost.Position;
        var completionRatio = duration.TotalSeconds <= 0 ? 0 : position.TotalSeconds / duration.TotalSeconds;
        _resumeEntries.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));

        if (forceRemoveCompleted || completionRatio >= 0.95 || position < TimeSpan.FromMinutes(2))
        {
            _settingsFacade.SaveResumeEntries(_resumeEntries);
            return;
        }

        _resumeEntries.Add(new PlaybackResumeEntry
        {
            Path = path,
            PositionSeconds = position.TotalSeconds,
            DurationSeconds = duration.TotalSeconds,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        _settingsFacade.SaveResumeEntries(_resumeEntries);
    }

    private void TryApplyResumePosition()
    {
        if (!ViewModel.Settings.ResumeEnabled)
        {
            return;
        }

        var path = PlayerHost.Source?.LocalPath;
        var duration = PlayerHost.NaturalDuration.HasTimeSpan ? PlayerHost.NaturalDuration.TimeSpan : TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(path) || duration <= TimeSpan.Zero)
        {
            return;
        }

        var entry = _resumeEntries
            .Where(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (entry is null)
        {
            return;
        }

        if (entry.PositionSeconds < TimeSpan.FromMinutes(2).TotalSeconds || entry.PositionSeconds >= duration.TotalSeconds * 0.95)
        {
            return;
        }

        var resumePosition = TimeSpan.FromSeconds(Math.Clamp(entry.PositionSeconds, 0, duration.TotalSeconds));
        PlayerHost.Position = resumePosition;
        UpdatePositionSurfaces(resumePosition, duration);
        ShowStatus($"Resumed: {Path.GetFileName(path)}");
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
        if (SubtitleRenderModeFlyoutSubItem is null)
        {
            return;
        }

        var checkedMode = GetEffectiveSubtitleRenderMode(snapshot);
        foreach (var item in SubtitleRenderModeFlyoutSubItem.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = item.Tag is SubtitleRenderMode mode && mode == checkedMode;
        }
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
        _subtitleOverlayWindow?.ApplyStyle(style);
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
        PlayerHost.SetAspectRatio(aspectRatio);
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
        PlayerHost.SetHardwareDecodingMode(mode);
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

        PlayerHost.SelectAudioTrack(trackId);
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

        var currentPath = PlayerHost.Source?.LocalPath;
        if (item.Tag is string offValue && offValue == "off")
        {
            PlayerHost.SelectSubtitleTrack(null);
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

            PlayerHost.SelectSubtitleTrack(null);
            ApplyTrackSelection(MediaTrackKind.Subtitle, null);
            RebuildEmbeddedSubtitleTrackFlyout();
            var result = await _subtitleWorkflowController.ImportEmbeddedSubtitleTrackAsync(currentPath, track);
            ShowStatus(result.CueCount > 0
                ? $"Imported embedded subtitle track {track.Id}."
                : "Embedded subtitle import failed.",
                result.CueCount == 0);
            return;
        }

        PlayerHost.SelectSubtitleTrack(trackId);
        ApplyTrackSelection(MediaTrackKind.Subtitle, trackId);
        RebuildEmbeddedSubtitleTrackFlyout();
        ShowStatus("Selected image-based embedded subtitle track for direct playback.");
    }

    private void AdjustSubtitleDelay(double delta)
    {
        _subtitleDelaySeconds += delta;
        PlayerHost.SetSubtitleDelay(_subtitleDelaySeconds);
        UpdateDelayFlyoutLabels();
        SaveCurrentSettings();
        ShowStatus($"Subtitle delay: {_subtitleDelaySeconds:+0.00;-0.00;0.00}s");
    }

    private void ResetSubtitleDelay()
    {
        _subtitleDelaySeconds = 0;
        PlayerHost.SetSubtitleDelay(_subtitleDelaySeconds);
        UpdateDelayFlyoutLabels();
        SaveCurrentSettings();
        ShowStatus("Subtitle delay reset.");
    }

    private void AdjustAudioDelay(double delta)
    {
        _audioDelaySeconds += delta;
        PlayerHost.SetAudioDelay(_audioDelaySeconds);
        UpdateDelayFlyoutLabels();
        SaveCurrentSettings();
        ShowStatus($"Audio delay: {_audioDelaySeconds:+0.00;-0.00;0.00}s");
    }

    private void ResetAudioDelay()
    {
        _audioDelaySeconds = 0;
        PlayerHost.SetAudioDelay(_audioDelaySeconds);
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
        if (!ResumePlaybackToggleItem.IsChecked)
        {
            _resumeEntries.Clear();
            _settingsFacade.SaveResumeEntries(_resumeEntries);
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
        if (sender is not MenuFlyoutItem { Tag: string colorHex, Text: string label })
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

    private async Task LoadPlaylistItemAsync(PlaylistItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            return;
        }

        _subtitleSourceOnlyOverrideVideoPath = null;
        SaveResumePosition();
        _resumeTimer.Stop();
        _autoResumePlaybackAfterCaptionReady = false;
        _autoResumePlaybackPath = null;
        _autoResumePlaybackPosition = TimeSpan.Zero;
        _autoResumePlaybackFromBeginning = true;
        _playbackSessionController.StartWith(item);
        RefreshPlaylistView();
        _pendingAutoFitPath = item.Path;
        _lastAutoFitSignature = null;
        PlayerHost.Source = new Uri(item.Path);
        PlayerHost.SetHardwareDecodingMode(ViewModel.Settings.HardwareDecodingMode);
        PlayerHost.SetPlaybackRate(ViewModel.Transport.PlaybackRate);
        PlayerHost.SetAspectRatio(_selectedAspectRatio);
        PlayerHost.SetAudioDelay(_audioDelaySeconds);
        PlayerHost.SetSubtitleDelay(_subtitleDelaySeconds);
        PlayerHost.SetZoom(0);
        PlayerHost.SetPan(0, 0);
        PlayerHost.Volume = ViewModel.Transport.Volume;
        PlayerHost.RequestHostBoundsSync();
        await _subtitleWorkflowController.LoadMediaSubtitlesAsync(item.Path);
    }

    private void RefreshPlaylistView()
    {
        ViewModel.Playlist.Items.Clear();
        foreach (var item in _playlistController.Items)
        {
            ViewModel.Playlist.Items.Add(item);
        }

        ViewModel.Playlist.CurrentIndex = _playlistController.CurrentIndex;
        if (_playlistController.CurrentIndex >= 0 && _playlistController.CurrentIndex < ViewModel.Playlist.Items.Count)
        {
            PlaylistList.SelectedIndex = _playlistController.CurrentIndex;
            ViewModel.Playlist.SelectedItem = ViewModel.Playlist.Items[_playlistController.CurrentIndex];
        }

        PlaylistSummaryTextBlock.Text = ViewModel.Playlist.Items.Count == 0
            ? "No queued items"
            : $"{ViewModel.Playlist.Items.Count} queued item(s)";
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
            BrowserPaneToggle.IsChecked = ViewModel.Browser.IsVisible;
            PlaylistPaneToggle.IsChecked = ViewModel.Playlist.IsVisible;
            return;
        }

        var showBrowser = ViewModel.Browser.IsVisible && width >= 900;
        var showPlaylist = ViewModel.Playlist.IsVisible && width >= 1160;

        BrowserPane.Visibility = showBrowser ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPane.Visibility = showPlaylist ? Visibility.Visible : Visibility.Collapsed;
        BrowserColumn.Width = showBrowser ? new GridLength(280) : new GridLength(0);
        PlaylistColumn.Width = showPlaylist ? new GridLength(320) : new GridLength(0);
        BrowserPaneToggle.IsChecked = ViewModel.Browser.IsVisible;
        PlaylistPaneToggle.IsChecked = ViewModel.Playlist.IsVisible;
    }

    private void ApplyAdaptiveStandardLayout(double height)
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Standard || height <= 0)
        {
            return;
        }

        var hideStatus = height < 760;
        var compactTransport = height < 700;
        var hideTransport = height < 620;

        StatusInfoBar.Visibility = hideStatus ? Visibility.Collapsed : Visibility.Visible;
        TransportPane.Visibility = hideTransport ? Visibility.Collapsed : Visibility.Visible;
        TransportPane.Margin = compactTransport ? new Thickness(8, 0, 8, 8) : new Thickness(16, 0, 16, 16);
        TransportPane.Padding = compactTransport ? new Thickness(10, 8, 10, 8) : new Thickness(16, 14, 16, 14);
        ShellContentGrid.Padding = compactTransport ? new Thickness(12, 8, 12, 8) : new Thickness(16, 12, 16, 12);
        PlayerPane.MinHeight = compactTransport ? 240 : 280;
    }

    private void TryApplyStandardAutoFit()
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Standard)
        {
            return;
        }

        var sourcePath = PlayerHost.Source?.LocalPath;
        if (string.IsNullOrWhiteSpace(sourcePath) || (_pendingAutoFitPath is not null && !string.Equals(_pendingAutoFitPath, sourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var snapshot = PlayerHost.Snapshot;
        var displayWidth = snapshot.VideoDisplayWidth > 0 ? snapshot.VideoDisplayWidth : snapshot.VideoWidth;
        var displayHeight = snapshot.VideoDisplayHeight > 0 ? snapshot.VideoDisplayHeight : snapshot.VideoHeight;
        if (displayWidth <= 0 || displayHeight <= 0 || PlayerPane.ActualWidth <= 0 || PlayerHost.ActualHeight <= 0)
        {
            return;
        }

        var signature = $"{sourcePath}|{displayWidth}x{displayHeight}";
        if (string.Equals(signature, _lastAutoFitSignature, StringComparison.Ordinal))
        {
            return;
        }

        var currentBounds = _windowModeService.CurrentBounds;
        var workArea = _windowModeService.GetCurrentDisplayBounds(workArea: true);
        var stageHeight = Math.Max(PlayerHost.ActualHeight, 1);
        var playerChromeWidth = Math.Max(PlayerPane.ActualWidth - PlayerHost.ActualWidth, 0);
        var nonPlayerWidth = Math.Max(currentBounds.Width - PlayerPane.ActualWidth, 0);
        var desiredStageWidth = stageHeight * displayWidth / displayHeight;
        var desiredWindowWidth = (int)Math.Round(nonPlayerWidth + playerChromeWidth + desiredStageWidth);
        var minimumWindowWidth = (int)Math.Ceiling(nonPlayerWidth + playerChromeWidth + 420);
        desiredWindowWidth = Math.Clamp(desiredWindowWidth, minimumWindowWidth, workArea.Width);

        var desiredWindowHeight = Math.Clamp(currentBounds.Height, 700, workArea.Height);
        var x = workArea.X + Math.Max((workArea.Width - desiredWindowWidth) / 2, 0);
        var y = workArea.Y + Math.Max((workArea.Height - desiredWindowHeight) / 2, 0);
        var desiredBounds = new RectInt32(x, y, desiredWindowWidth, desiredWindowHeight);
        _windowModeService.ApplyStandardBounds(desiredBounds);
        _lastAutoFitSignature = signature;
        _pendingAutoFitPath = null;
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
        ShowFullscreenOverlay();
    }

    private async Task ExitFullscreenAsync()
    {
        HideFullscreenOverlay();
        await SetWindowModeAsync(PlaybackWindowMode.Standard);
    }

    private void ApplyWindowModeChrome(PlaybackWindowMode mode)
    {
        var isPlayerOnly = mode is PlaybackWindowMode.Fullscreen or PlaybackWindowMode.PictureInPicture;
        var isNonStandard = mode != PlaybackWindowMode.Standard;
        AppTitleBar.Visibility = isNonStandard ? Visibility.Collapsed : Visibility.Visible;
        ShellCommandBar.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        StatusInfoBar.Visibility = isNonStandard ? Visibility.Collapsed : Visibility.Visible;
        TransportPane.Visibility = isPlayerOnly ? Visibility.Collapsed : Visibility.Visible;
        ShellContentGrid.Padding = isNonStandard ? new Thickness(0) : new Thickness(16, 12, 16, 12);
        ShellContentGrid.ColumnSpacing = isNonStandard ? 0 : 16;
        PlayerPane.Padding = isNonStandard ? new Thickness(0) : new Thickness(18);
        PlayerPane.BorderThickness = isNonStandard ? new Thickness(0) : new Thickness(1);
        PlayerPane.CornerRadius = isNonStandard ? new CornerRadius(0) : new CornerRadius(24);
        DecoderBadge.Visibility = mode == PlaybackWindowMode.Fullscreen ? Visibility.Collapsed : Visibility.Visible;
        SubtitleOverlayBorder.Margin = mode == PlaybackWindowMode.Fullscreen
            ? new Thickness(32, 0, 32, 110)
            : new Thickness(24);
        if (mode == PlaybackWindowMode.Fullscreen)
        {
            ShowFullscreenOverlay();
        }
        else
        {
            HideFullscreenOverlay();
        }

        SyncPaneLayout(RootGrid.ActualWidth);
        if (mode == PlaybackWindowMode.Standard)
        {
            ApplyAdaptiveStandardLayout(RootGrid.ActualHeight);
            TryApplyStandardAutoFit();
        }

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

    private void EnsureFullscreenOverlayWindow()
    {
        if (_fullscreenOverlayWindow is not null)
        {
            return;
        }

        _fullscreenOverlayWindow = new FullscreenOverlayWindow(WindowNative.GetWindowHandle(this));
        _fullscreenOverlayWindow.ActivityDetected += FullscreenOverlayWindow_ActivityDetected;
        _fullscreenOverlayWindow.InteractionStateChanged += FullscreenOverlayWindow_InteractionStateChanged;
        OverlayPlayPauseButton = _fullscreenOverlayWindow.PlayPauseButton;
        OverlayPlayPauseButton.Click += PlayPauseButton_Click;
        OverlaySubtitleToggleButton = _fullscreenOverlayWindow.SubtitleToggleButton;
        OverlaySubtitleToggleButton.Click += OverlaySubtitleToggleButton_Click;
        _fullscreenOverlayWindow.SeekBackButton.Click += SeekBack_Click;
        _fullscreenOverlayWindow.SeekForwardButton.Click += SeekForward_Click;
        _fullscreenOverlayWindow.ExitFullscreenButton.Click += ExitFullscreenOverlayButton_Click;
        FullscreenPositionSlider = _fullscreenOverlayWindow.PositionSlider;
        FullscreenPositionSlider.ValueChanged += FullscreenPositionSlider_ValueChanged;
        AttachScrubberHandlers(FullscreenPositionSlider);
        FullscreenCurrentTimeTextBlock = _fullscreenOverlayWindow.CurrentTimeTextBlock;
        FullscreenDurationTextBlock = _fullscreenOverlayWindow.DurationTextBlock;
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

    private void ShowFullscreenOverlay()
    {
        if (_modalUiSuppressionCount > 0)
        {
            _fullscreenControlsTimer.Stop();
            _fullscreenOverlayWindow?.HideOverlay();
            return;
        }

        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen || !_isWindowActive)
        {
            return;
        }

        EnsureFullscreenOverlayWindow();
        if (!_isFullscreenOverlayVisible)
        {
            PositionFullscreenOverlay();
            _fullscreenOverlayWindow!.ShowOverlay(_windowModeService.GetCurrentDisplayBounds());
            _isFullscreenOverlayVisible = true;
        }
        else
        {
            PositionFullscreenOverlay();
        }

        UpdateSubtitleVisibility();
        ScheduleFullscreenOverlayAutoHide();
    }

    private void HideFullscreenOverlay()
    {
        _isFullscreenOverlayVisible = false;
        _fullscreenControlsTimer.Stop();
        _fullscreenOverlayWindow?.HideOverlay();
        UpdateSubtitleVisibility();
    }

    private void PositionFullscreenOverlay()
    {
        if (_fullscreenOverlayWindow is null)
        {
            return;
        }

        _fullscreenOverlayWindow.PositionOverlay(_windowModeService.GetCurrentDisplayBounds());
    }

    private void ScheduleFullscreenOverlayAutoHide()
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen || !_isFullscreenOverlayVisible || _isPositionScrubbing || _isFullscreenOverlayInteracting)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_fullscreenControlsTimer.IsEnabled && now - _lastOverlayTimerResetTick < 140)
        {
            return;
        }

        _lastOverlayTimerResetTick = now;
        _fullscreenControlsTimer.Stop();
        _fullscreenControlsTimer.Start();
    }

    private void FullscreenOverlayWindow_ActivityDetected()
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen)
        {
            return;
        }

        RegisterFullscreenOverlayInteraction();
        ShowFullscreenOverlay();
    }

    private void FullscreenOverlayWindow_InteractionStateChanged(bool isInteracting)
    {
        _isFullscreenOverlayInteracting = isInteracting;
        RegisterFullscreenOverlayInteraction();
        if (isInteracting)
        {
            _fullscreenControlsTimer.Stop();
            return;
        }

        ScheduleFullscreenOverlayAutoHide();
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
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen)
        {
            return;
        }

        ShowFullscreenOverlay();
    }

    private void FullscreenControlsTimer_Tick(object? sender, object e)
    {
        _fullscreenControlsTimer.Stop();
        if (Environment.TickCount64 < _fullscreenOverlayHideBlockedUntilTick)
        {
            ScheduleFullscreenOverlayAutoHide();
            return;
        }

        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen && !_isPositionScrubbing && !_isFullscreenOverlayInteracting)
        {
            HideFullscreenOverlay();
        }
    }

    private void RegisterFullscreenOverlayInteraction(int holdMilliseconds = 1800)
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen)
        {
            return;
        }

        _fullscreenOverlayHideBlockedUntilTick = Math.Max(_fullscreenOverlayHideBlockedUntilTick, Environment.TickCount64 + holdMilliseconds);
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
        UpdateSubtitleOverlayWindow(showSource, showPrimary);
        UpdateOverlayControlState();
    }

    private void EnsureSubtitleOverlayWindow()
    {
        _subtitleOverlayWindow ??= new SubtitleOverlayWindow(WindowNative.GetWindowHandle(this));
    }

    private void UpdateSubtitleOverlayWindow(bool showSource, bool showTranslation)
    {
        if (_modalUiSuppressionCount > 0)
        {
            _subtitleOverlayWindow?.HideOverlay();
            return;
        }

        if (PlayerHost.Source is null)
        {
            _subtitleOverlayWindow?.HideOverlay();
            return;
        }

        if (!_isWindowActive)
        {
            _subtitleOverlayWindow?.HideOverlay();
            return;
        }

        var hasSource = showSource && !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.SourceText);
        var hasTranslation = showTranslation && !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.TranslationText);
        if (!hasSource && !hasTranslation)
        {
            _subtitleOverlayWindow?.HideOverlay();
            return;
        }

        var stageBounds = GetPlayerStageScreenBounds();
        if (stageBounds.Width <= 0 || stageBounds.Height <= 0)
        {
            _subtitleOverlayWindow?.HideOverlay();
            return;
        }

        EnsureSubtitleOverlayWindow();
        _subtitleOverlayWindow!.ApplyStyle(ViewModel.Settings.SubtitleStyle);
        _subtitleOverlayWindow!.SetContent(
            ViewModel.SubtitleOverlay.SourceText,
            ViewModel.SubtitleOverlay.TranslationText,
            hasSource,
            hasTranslation);
        _subtitleOverlayWindow.ShowOverlay(stageBounds, GetSubtitleOverlayBottomOffset());
    }

    private int GetSubtitleOverlayBottomOffset()
    {
        var styleOffset = (int)Math.Round(ViewModel.Settings.SubtitleStyle.BottomMargin);
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            return (_isFullscreenOverlayVisible ? 248 : 68) + styleOffset;
        }

        return 44 + styleOffset;
    }

    private IDisposable SuppressModalUi()
    {
        _modalUiSuppressionCount++;
        _fullscreenControlsTimer.Stop();
        _fullscreenOverlayWindow?.HideOverlay();
        _subtitleOverlayWindow?.HideOverlay();
        return new ModalUiSuppressionScope(this);
    }

    private IDisposable SuppressDialogPresentation()
    {
        var hostSuppression = PlayerHost?.SuppressNativeHost();
        var modalSuppression = SuppressModalUi();
        return new CombinedSuppressionScope(hostSuppression, modalSuppression);
    }

    private void ReleaseModalUiSuppression()
    {
        if (_modalUiSuppressionCount == 0)
        {
            return;
        }

        _modalUiSuppressionCount--;
        if (_modalUiSuppressionCount > 0)
        {
            return;
        }

        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen && _isFullscreenOverlayVisible)
        {
            ShowFullscreenOverlay();
            return;
        }

        UpdateSubtitleVisibility();
    }

    private RectInt32 GetPlayerStageScreenBounds()
    {
        if (PlayerHost.XamlRoot is null)
        {
            return default;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        var topLeft = new NativePoint();
        if (!NativeMethods.ClientToScreen(hwnd, ref topLeft))
        {
            return default;
        }

        var transform = PlayerHost.TransformToVisual(RootGrid);
        var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = PlayerHost.XamlRoot.RasterizationScale;
        var x = topLeft.X + Math.Max((int)Math.Round(origin.X * scale), 0);
        var y = topLeft.Y + Math.Max((int)Math.Round(origin.Y * scale), 0);
        var width = Math.Max((int)Math.Round(PlayerHost.ActualWidth * scale), 0);
        var height = Math.Max((int)Math.Round(PlayerHost.ActualHeight * scale), 0);
        return width <= 0 || height <= 0
            ? default
            : new RectInt32(x, y, width, height);
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
            ShowFullscreenOverlay();
            _fullscreenControlsTimer.Stop();
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
            PlayerHost.Position = TimeSpan.FromSeconds(slider.Value);
        }

        _isPositionScrubbing = false;
        _activeScrubber = null;
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            ShowFullscreenOverlay();
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && IsAppStillForeground())
        {
            _isWindowActive = true;
            return;
        }

        _isWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _fullscreenOverlayWindow?.HideOverlay();
            _subtitleOverlayWindow?.HideOverlay();
            return;
        }

        TryApplySystemBackdrop();
        UpdateSubtitleVisibility();
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen && _isFullscreenOverlayVisible)
        {
            PositionFullscreenOverlay();
            _fullscreenOverlayWindow?.ShowOverlay(_windowModeService.GetCurrentDisplayBounds());
        }
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
        var snapshot = _subtitleWorkflowController.Snapshot;
        var currentPath = PlayerHost.Source?.LocalPath;
        if (snapshot.SubtitleSource != SubtitlePipelineSource.Generated || string.IsNullOrWhiteSpace(currentPath))
        {
            await Task.CompletedTask;
            return;
        }

        _autoResumePlaybackAfterCaptionReady = true;
        _autoResumePlaybackPath = currentPath;
        _autoResumePlaybackPosition = PlayerHost.Position;
        _autoResumePlaybackFromBeginning = false;
        await PlayerHost.PauseAsync();
        await WaitForPauseStateAsync(paused: true);
        ViewModel.Transport.IsPaused = true;
        PlayPauseButton.Content = "Play";
        UpdateOverlayControlState();
        ShowStatus("Refreshing captions for the selected transcription model.");
    }

    private async Task WaitForPauseStateAsync(bool paused)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (PlayerHost.IsPaused == paused)
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private void UpdateCaptionStartupGate(SubtitleWorkflowSnapshot snapshot)
    {
        var currentPath = PlayerHost.Source?.LocalPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !string.Equals(snapshot.CurrentVideoPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            _autoResumePlaybackAfterCaptionReady = false;
            _autoResumePlaybackPath = null;
            _autoResumePlaybackPosition = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;
            return;
        }

        var shouldPauseForInitialCaptions = snapshot.SubtitleSource == SubtitlePipelineSource.Generated
            && snapshot.IsCaptionGenerationInProgress
            && snapshot.Cues.Count == 0
            && PlayerHost.Source is not null
            && PlayerHost.Position <= TimeSpan.FromSeconds(2);

        if (shouldPauseForInitialCaptions && !_autoResumePlaybackAfterCaptionReady)
        {
            _autoResumePlaybackAfterCaptionReady = true;
            _autoResumePlaybackPath = currentPath;
            _autoResumePlaybackPosition = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;
            PlayerHost.Pause();
            ViewModel.Transport.IsPaused = true;
            PlayPauseButton.Content = "Play";
            UpdateOverlayControlState();
            ShowStatus("Generating initial captions before playback starts.");
            return;
        }

        if (_autoResumePlaybackAfterCaptionReady
            && string.Equals(_autoResumePlaybackPath, currentPath, StringComparison.OrdinalIgnoreCase)
            && snapshot.Cues.Count > 0)
        {
            _autoResumePlaybackAfterCaptionReady = false;
            _autoResumePlaybackPath = null;
            PlayerHost.Position = _autoResumePlaybackFromBeginning ? TimeSpan.Zero : _autoResumePlaybackPosition;
            _autoResumePlaybackPosition = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;
            PlayerHost.Play();
            ViewModel.Transport.IsPaused = false;
            PlayPauseButton.Content = "Pause";
            UpdateOverlayControlState();
            ShowStatus("Captions ready. Playing with generated subtitles.");
            return;
        }

        if (!snapshot.IsCaptionGenerationInProgress)
        {
            _autoResumePlaybackAfterCaptionReady = false;
            _autoResumePlaybackPath = null;
            _autoResumePlaybackPosition = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;
        }
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

    private sealed class ModalUiSuppressionScope : IDisposable
    {
        private MainWindow? _owner;

        public ModalUiSuppressionScope(MainWindow owner)
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
