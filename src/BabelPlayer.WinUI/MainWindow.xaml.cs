using System.IO;
using System.Linq;
using Microsoft.UI;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace BabelPlayer.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly SettingsFacade _settingsFacade = new();
    private readonly LibraryBrowserService _libraryBrowserService = new();
    private readonly PlaylistController _playlistController = new();
    private readonly PlaybackSessionController _playbackSessionController;
    private readonly CredentialFacade _credentialFacade = new();
    private readonly SubtitleWorkflowController _subtitleWorkflowController;
    private readonly DispatcherTimer _transportTimer;
    private readonly DispatcherTimer _fullscreenControlsTimer;
    private readonly IFilePickerService _filePickerService;
    private readonly WinUIWindowModeService _windowModeService;
    private readonly WinUICredentialDialogService _credentialDialogService;
    private readonly IRuntimeBootstrapService _runtimeBootstrapService;
    private bool _suppressPositionSliderChanges;
    private bool _suppressFullscreenSliderChanges;
    private bool _suppressWorkflowControlEvents;
    private bool _suppressWindowModeButtonChanges;
    private bool _isPositionScrubbing;
    private Slider? _activeScrubber;
    private SubtitleRenderMode _lastNonOffSubtitleRenderMode = SubtitleRenderMode.TranslationOnly;
    private Border AppTitleBar = null!;
    private TextBlock WindowTitleTextBlock = null!;
    private ToggleButton ThemeToggleButton = null!;
    private CommandBar ShellCommandBar = null!;
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
    private Border FullscreenControlsOverlay = null!;
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
    private Button OverlayPlayPauseButton = null!;
    private Button OverlaySubtitleToggleButton = null!;
    private Slider FullscreenPositionSlider = null!;
    private TextBlock FullscreenCurrentTimeTextBlock = null!;
    private TextBlock FullscreenDurationTextBlock = null!;

    public MainShellViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        _playbackSessionController = new PlaybackSessionController(_playlistController);
        _filePickerService = new WinUIFilePickerService(this);
        _windowModeService = new WinUIWindowModeService(this);
        _runtimeBootstrapService = new RuntimeBootstrapService();
        _transportTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _fullscreenControlsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };

        BuildShell();
        _credentialDialogService = new WinUICredentialDialogService(RootGrid);
        _subtitleWorkflowController = new SubtitleWorkflowController(
            _credentialFacade,
            _credentialDialogService,
            _filePickerService,
            _runtimeBootstrapService);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        PlayerHost.Initialize(this);
        PlaylistList.ItemsSource = ViewModel.Playlist.Items;

        _transportTimer.Tick += TransportTimer_Tick;
        _fullscreenControlsTimer.Tick += FullscreenControlsTimer_Tick;
        _transportTimer.Start();

        PlayerHost.MediaOpened += PlayerHost_MediaOpened;
        PlayerHost.MediaEnded += PlayerHost_MediaEnded;
        PlayerHost.MediaFailed += PlayerHost_MediaFailed;
        PlayerHost.TracksChanged += PlayerHost_TracksChanged;
        PlayerHost.RuntimeInstallProgress += PlayerHost_RuntimeInstallProgress;
        _subtitleWorkflowController.StatusChanged += SubtitleWorkflowController_StatusChanged;
        _subtitleWorkflowController.SnapshotChanged += SubtitleWorkflowController_SnapshotChanged;

        Closed += MainWindow_Closed;

        InitializeShellState();
        _ = _subtitleWorkflowController.InitializeAsync();
    }

    private void BuildShell()
    {
        RootGrid.AllowDrop = true;
        RootGrid.DragOver += RootGrid_DragOver;
        RootGrid.Drop += RootGrid_Drop;
        RootGrid.SizeChanged += RootGrid_SizeChanged;
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
        ShellCommandBar.PrimaryCommands.Add(CreatePrimaryCommand("Subtitles", Symbol.Edit, ImportSubtitle_Click));
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
        BrowserPaneToggle = new AppBarToggleButton { Label = "Browser Pane" };
        BrowserPaneToggle.Click += BrowserPaneToggle_Click;
        PlaylistPaneToggle = new AppBarToggleButton { Label = "Playlist Pane" };
        PlaylistPaneToggle.Click += PlaylistPaneToggle_Click;
        ShellCommandBar.SecondaryCommands.Add(BrowserPaneToggle);
        ShellCommandBar.SecondaryCommands.Add(PlaylistPaneToggle);
        ShellCommandBar.SecondaryCommands.Add(new AppBarButton { Label = "Add Videos Root" });
        ((AppBarButton)ShellCommandBar.SecondaryCommands[^1]).Click += AddRootFolder_Click;
        RootGrid.Children.Add(ShellCommandBar);

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

        FullscreenControlsOverlay = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(24),
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(ColorHelper.FromArgb(220, 12, 18, 28)),
            MaxWidth = 1120,
            Visibility = Visibility.Collapsed
        };
        var overlayRoot = new StackPanel
        {
            Spacing = 12
        };
        var overlayStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12
        };
        var overlayBackButton = new Button { Content = "<<" };
        overlayBackButton.Click += SeekBack_Click;
        overlayStack.Children.Add(overlayBackButton);
        OverlayPlayPauseButton = new Button { Content = "Play", MinWidth = 84 };
        OverlayPlayPauseButton.Click += PlayPauseButton_Click;
        overlayStack.Children.Add(OverlayPlayPauseButton);
        var overlayForwardButton = new Button { Content = ">>" };
        overlayForwardButton.Click += SeekForward_Click;
        overlayStack.Children.Add(overlayForwardButton);
        OverlaySubtitleToggleButton = new Button { Content = "Subtitles On" };
        OverlaySubtitleToggleButton.Click += OverlaySubtitleToggleButton_Click;
        overlayStack.Children.Add(OverlaySubtitleToggleButton);
        var overlayExitButton = new Button { Content = "Exit Fullscreen" };
        overlayExitButton.Click += ExitFullscreenOverlayButton_Click;
        overlayStack.Children.Add(overlayExitButton);
        overlayRoot.Children.Add(overlayStack);

        var scrubberGrid = new Grid
        {
            ColumnSpacing = 12
        };
        scrubberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scrubberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrubberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FullscreenCurrentTimeTextBlock = new TextBlock
        {
            Text = "00:00",
            VerticalAlignment = VerticalAlignment.Center
        };
        scrubberGrid.Children.Add(FullscreenCurrentTimeTextBlock);

        FullscreenPositionSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            MinWidth = 420
        };
        FullscreenPositionSlider.ValueChanged += FullscreenPositionSlider_ValueChanged;
        AttachScrubberHandlers(FullscreenPositionSlider);
        Grid.SetColumn(FullscreenPositionSlider, 1);
        scrubberGrid.Children.Add(FullscreenPositionSlider);

        FullscreenDurationTextBlock = new TextBlock
        {
            Text = "00:00",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(FullscreenDurationTextBlock, 2);
        scrubberGrid.Children.Add(FullscreenDurationTextBlock);

        overlayRoot.Children.Add(scrubberGrid);
        FullscreenControlsOverlay.Child = overlayRoot;
        grid.Children.Add(FullscreenControlsOverlay);

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
        for (var index = 0; index < 9; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = index switch
                {
                    5 => new GridLength(1, GridUnitType.Star),
                    7 => new GridLength(150),
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

        var forwardButton = new Button { Content = new FontIcon { Glyph = "\uE893" } };
        forwardButton.Click += SeekForward_Click;
        Grid.SetColumn(forwardButton, 3);
        grid.Children.Add(forwardButton);

        var nextButton = new Button { Content = new FontIcon { Glyph = "\uE893" } };
        nextButton.Click += NextTrack_Click;
        Grid.SetColumn(nextButton, 4);
        grid.Children.Add(nextButton);

        PositionSlider = new Slider { Minimum = 0, Maximum = 1 };
        PositionSlider.ValueChanged += PositionSlider_ValueChanged;
        AttachScrubberHandlers(PositionSlider);
        Grid.SetColumn(PositionSlider, 5);
        grid.Children.Add(PositionSlider);

        TimeTextBlock = new TextBlock { Text = "00:00 / 00:00", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(TimeTextBlock, 6);
        grid.Children.Add(TimeTextBlock);

        VolumeSlider = new Slider { Minimum = 0, Maximum = 1, Value = 0.8 };
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        Grid.SetColumn(VolumeSlider, 7);
        grid.Children.Add(VolumeSlider);

        MuteToggleButton = new ToggleButton { Content = new FontIcon { Glyph = "\uE767" } };
        MuteToggleButton.Click += MuteToggleButton_Click;
        Grid.SetColumn(MuteToggleButton, 8);
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
                     SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny"),
                     SubtitleWorkflowCatalog.GetTranscriptionModel("local:base"),
                     SubtitleWorkflowCatalog.GetTranscriptionModel("local:small"),
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

        ViewModel.Settings = settings;
        ViewModel.WindowTitle = "Babel Player";
        ViewModel.StatusMessage = "Open local media, folders, or subtitle files to start the WinUI migration shell.";
        ViewModel.IsStatusOpen = true;
        ViewModel.ActiveHardwareDecoder = "mpv ready";
        ViewModel.Browser.IsVisible = settings.ShowBrowserPanel;
        ViewModel.Playlist.IsVisible = settings.ShowPlaylistPanel;
        ViewModel.Transport.Volume = 0.8;
        ViewModel.Transport.PlaybackRate = settings.DefaultPlaybackRate;
        _lastNonOffSubtitleRenderMode = settings.SubtitleRenderMode == SubtitleRenderMode.Off
            ? SubtitleRenderMode.TranslationOnly
            : settings.SubtitleRenderMode;
        ViewModel.SubtitleOverlay.ShowSource = settings.SubtitleRenderMode is SubtitleRenderMode.SourceOnly or SubtitleRenderMode.Dual;
        ViewModel.SubtitleOverlay.TranslationText = "Drop a file or choose Open to start playback.";
        ViewModel.SelectedTranscriptionLabel = SubtitleWorkflowCatalog.GetTranscriptionModel(SubtitleWorkflowCatalog.DefaultTranscriptionModelKey).DisplayName;
        ViewModel.SelectedTranslationLabel = SubtitleWorkflowCatalog.GetTranslationModel(null).DisplayName;
        WindowTitleTextBlock.Text = ViewModel.WindowTitle;
        TranslatedSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.TranslationText;
        StatusInfoBar.IsOpen = true;
        StatusInfoBar.Message = ViewModel.StatusMessage;

        ThemeToggleButton.IsChecked = true;
        ApplyTheme(isDark: true);
        VolumeSlider.Value = ViewModel.Transport.Volume;
        SpeedComboBox.SelectedIndex = 1;
        _windowModeService.SetModeAsync(PlaybackWindowMode.Standard).GetAwaiter().GetResult();
        ApplyWindowModeChrome(PlaybackWindowMode.Standard);
        SyncWindowModeButtons(PlaybackWindowMode.Standard);
        UpdateOverlayControlState();
        TranscriptionModelComboBox.SelectedIndex = 1;
        TranslationModelComboBox.SelectedIndex = -1;
        TranslationToggleSwitch.IsOn = false;
        AutoTranslateToggleSwitch.IsOn = false;
        BrowserPaneToggle.IsChecked = ViewModel.Browser.IsVisible;
        PlaylistPaneToggle.IsChecked = ViewModel.Playlist.IsVisible;

        foreach (var root in _libraryBrowserService.BuildPinnedRoots(settings.PinnedRoots))
        {
            ViewModel.Browser.Roots.Add(root);
        }

        RebuildLibraryTree();
        SyncPaneLayout(RootGrid.ActualWidth);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SaveCurrentSettings();
    }

    private void SaveCurrentSettings()
    {
        var pinnedRoots = ViewModel.Browser.Roots.Select(root => root.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var updatedSettings = _settingsFacade.UpdatePlaybackDefaults(
            _settingsFacade.UpdateLayout(
                ViewModel.Settings,
                ViewModel.Browser.IsVisible,
                ViewModel.Playlist.IsVisible,
                PlaybackWindowMode.Standard),
            PlayerHost.HardwareDecodingMode,
            ViewModel.Transport.PlaybackRate,
            0,
            0,
            "auto");

        ViewModel.Settings = updatedSettings with
        {
            PinnedRoots = pinnedRoots
        };

        _settingsFacade.Save(ViewModel.Settings);

        var resumeEntry = _playbackSessionController.BuildResumeEntry(new PlaybackStateSnapshot
        {
            Path = PlayerHost.Source?.LocalPath,
            Position = PlayerHost.Position,
            Duration = PlayerHost.NaturalDuration.HasTimeSpan ? PlayerHost.NaturalDuration.TimeSpan : TimeSpan.Zero
        });

        _settingsFacade.SaveResumeEntries(resumeEntry is null ? [] : [resumeEntry]);
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
    }

    private void PlaylistPaneToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Playlist.IsVisible = PlaylistPaneToggle.IsChecked == true;
        SyncPaneLayout(RootGrid.ActualWidth);
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

        var targetMode = _windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen
            ? PlaybackWindowMode.Standard
            : PlaybackWindowMode.Fullscreen;

        await SetWindowModeAsync(targetMode);
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
        await SetWindowModeAsync(PlaybackWindowMode.Standard);
    }

    private void OverlaySubtitleToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var subtitlesEnabled = ViewModel.Settings.SubtitleRenderMode != SubtitleRenderMode.Off;
        if (subtitlesEnabled)
        {
            _lastNonOffSubtitleRenderMode = ViewModel.Settings.SubtitleRenderMode;
        }

        var nextMode = subtitlesEnabled ? SubtitleRenderMode.Off : _lastNonOffSubtitleRenderMode;
        ViewModel.Settings = ViewModel.Settings with
        {
            SubtitleRenderMode = nextMode
        };
        ViewModel.SubtitleOverlay.ShowSource = nextMode is SubtitleRenderMode.SourceOnly or SubtitleRenderMode.Dual;
        UpdateSubtitleVisibility();
        UpdateOverlayControlState();
        ShowStatus(subtitlesEnabled ? "Subtitles hidden." : "Subtitles shown.");
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

    private void SeekBack_Click(object sender, RoutedEventArgs e) => PlayerHost.SeekBy(TimeSpan.FromSeconds(-10));

    private void SeekForward_Click(object sender, RoutedEventArgs e) => PlayerHost.SeekBy(TimeSpan.FromSeconds(10));

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlayerHost.IsPaused)
        {
            PlayerHost.Play();
            ShowStatus("Playback resumed.");
            return;
        }

        PlayerHost.Pause();
        ShowStatus("Playback paused.");
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
        if (_suppressFullscreenSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.5)
        {
            return;
        }

        PlayerHost.Position = TimeSpan.FromSeconds(e.NewValue);
        UpdateScrubTimeLabels(e.NewValue, FullscreenPositionSlider.Maximum);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ViewModel.Transport.Volume = e.NewValue;
        PlayerHost.Volume = e.NewValue;
    }

    private void MuteToggleButton_Click(object sender, RoutedEventArgs e)
    {
        PlayerHost.SetMute(MuteToggleButton.IsChecked == true);
    }

    private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedComboBox.SelectedItem is not string speedLabel || !double.TryParse(speedLabel.Replace("x", string.Empty), out var speed))
        {
            return;
        }

        ViewModel.Transport.PlaybackRate = speed;
        PlayerHost.SetPlaybackRate(speed);
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
        SyncPaneLayout(e.NewSize.Width);
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
            var current = _playlistController.CurrentItem;
            ViewModel.WindowTitle = current is null ? "Babel Player" : $"Babel Player - {current.DisplayName}";
            Title = ViewModel.WindowTitle;
            WindowTitleTextBlock.Text = ViewModel.WindowTitle;
            ShowStatus(current is null ? "Media opened." : $"Now playing {current.DisplayName}.");
        });
    }

    private void PlayerHost_MediaEnded()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
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
        DispatcherQueue.TryEnqueue(() => ShowStatus(message, true));
    }

    private void PlayerHost_TracksChanged(IReadOnlyList<MediaTrackInfo> tracks)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var audioTracks = tracks.Count(track => track.Kind == MediaTrackKind.Audio);
            var subtitleTracks = tracks.Count(track => track.Kind == MediaTrackKind.Subtitle);
            ShowStatus($"Tracks updated. Audio: {audioTracks}, subtitles: {subtitleTracks}.");
        });
    }

    private void PlayerHost_RuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        DispatcherQueue.TryEnqueue(() => ShowStatus($"Runtime setup: {progress.Stage}."));
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
        ViewModel.SelectedTranscriptionLabel = snapshot.SelectedTranscriptionLabel;
        ViewModel.SelectedTranslationLabel = snapshot.SelectedTranslationLabel;
        ViewModel.IsTranslationEnabled = snapshot.IsTranslationEnabled;
        ViewModel.IsAutoTranslateEnabled = snapshot.AutoTranslateEnabled;
        ViewModel.SubtitleSource = snapshot.SubtitleSource;
        ViewModel.IsCaptionGenerationInProgress = snapshot.IsCaptionGenerationInProgress;
        ViewModel.SubtitleOverlay.SelectedTranscriptionLabel = snapshot.SelectedTranscriptionLabel;
        ViewModel.SubtitleOverlay.SelectedTranslationLabel = snapshot.SelectedTranslationLabel;
        ViewModel.SubtitleOverlay.IsTranslationEnabled = snapshot.IsTranslationEnabled;
        ViewModel.SubtitleOverlay.IsAutoTranslateEnabled = snapshot.AutoTranslateEnabled;
        ViewModel.SubtitleOverlay.SubtitleSource = snapshot.SubtitleSource;
        ViewModel.SubtitleOverlay.IsCaptionGenerationInProgress = snapshot.IsCaptionGenerationInProgress;
        ViewModel.SubtitleOverlay.StatusText = snapshot.OverlayStatus ?? string.Empty;
        ViewModel.SubtitleOverlay.ShowSource = ViewModel.Settings.SubtitleRenderMode is SubtitleRenderMode.SourceOnly or SubtitleRenderMode.Dual;

        if (snapshot.ActiveCue is null)
        {
            ViewModel.SubtitleOverlay.SourceText = string.Empty;
            ViewModel.SubtitleOverlay.TranslationText = snapshot.OverlayStatus ?? string.Empty;
        }
        else
        {
            ViewModel.SubtitleOverlay.SourceText = snapshot.ActiveCue.SourceText;
            ViewModel.SubtitleOverlay.TranslationText = string.IsNullOrWhiteSpace(snapshot.ActiveCue.TranslatedText)
                ? snapshot.ActiveCue.SourceText
                : snapshot.ActiveCue.TranslatedText;
        }

        if (TranscriptionModelComboBox is not null)
        {
            TranscriptionModelComboBox.SelectedItem = TranscriptionModelComboBox.Items
                .OfType<TranscriptionModelSelection>()
                .FirstOrDefault(item => string.Equals(item.Key, snapshot.SelectedTranscriptionModelKey, StringComparison.Ordinal));
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
        }
        finally
        {
            _suppressWorkflowControlEvents = false;
        }

        UpdateSubtitleOverlay(PlayerHost.Position);
    }

    private void ApplyTheme(bool isDark)
    {
        ViewModel.IsDarkTheme = isDark;
        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
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

        _playbackSessionController.StartWith(item);
        RefreshPlaylistView();
        PlayerHost.Source = new Uri(item.Path);
        PlayerHost.SetHardwareDecodingMode(ViewModel.Settings.HardwareDecodingMode);
        PlayerHost.SetPlaybackRate(ViewModel.Transport.PlaybackRate);
        PlayerHost.SetAspectRatio(ViewModel.Settings.AspectRatioOverride);
        PlayerHost.SetZoom(0);
        PlayerHost.SetPan(0, 0);
        PlayerHost.Volume = ViewModel.Transport.Volume;
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

    private async Task SetWindowModeAsync(PlaybackWindowMode mode)
    {
        await _windowModeService.SetModeAsync(mode);
        ApplyWindowModeChrome(mode);
        SyncWindowModeButtons(mode);
        UpdateOverlayControlState();
        ShowStatus(mode switch
        {
            PlaybackWindowMode.Borderless => "Immersive mode enabled.",
            PlaybackWindowMode.Fullscreen => "Fullscreen enabled.",
            PlaybackWindowMode.PictureInPicture => "Picture in picture enabled.",
            _ => "Standard window mode restored."
        });
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
            FullscreenControlsOverlay.Visibility = Visibility.Visible;
            _fullscreenControlsTimer.Stop();
            _fullscreenControlsTimer.Start();
        }
        else
        {
            FullscreenControlsOverlay.Visibility = Visibility.Collapsed;
            _fullscreenControlsTimer.Stop();
        }

        SyncPaneLayout(RootGrid.ActualWidth);
    }

    private void SyncWindowModeButtons(PlaybackWindowMode mode)
    {
        _suppressWindowModeButtonChanges = true;
        ImmersiveToggleButton.IsChecked = mode == PlaybackWindowMode.Borderless;
        FullscreenToggleButton.IsChecked = mode == PlaybackWindowMode.Fullscreen;
        PictureInPictureToggleButton.IsChecked = mode == PlaybackWindowMode.PictureInPicture;
        _suppressWindowModeButtonChanges = false;
    }

    private void UpdateOverlayControlState()
    {
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

    private void PlayerPane_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_windowModeService.CurrentMode != PlaybackWindowMode.Fullscreen)
        {
            return;
        }

        FullscreenControlsOverlay.Visibility = Visibility.Visible;
        _fullscreenControlsTimer.Stop();
        _fullscreenControlsTimer.Start();
    }

    private void FullscreenControlsTimer_Tick(object? sender, object e)
    {
        _fullscreenControlsTimer.Stop();
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            FullscreenControlsOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSubtitleOverlay(TimeSpan position)
    {
        var snapshot = _subtitleWorkflowController.Snapshot;
        var currentCue = snapshot.ActiveCue;
        if (currentCue is null)
        {
            ViewModel.SubtitleOverlay.SourceText = string.Empty;
            ViewModel.SubtitleOverlay.TranslationText = _windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen
                ? string.Empty
                : snapshot.OverlayStatus ?? string.Empty;
            UpdateSubtitleVisibility();
            return;
        }

        ViewModel.SubtitleOverlay.SourceText = currentCue.SourceText;
        ViewModel.SubtitleOverlay.TranslationText = string.IsNullOrWhiteSpace(currentCue.TranslatedText)
            ? currentCue.SourceText
            : currentCue.TranslatedText;
        UpdateSubtitleVisibility();
    }

    private void UpdateSubtitleVisibility()
    {
        var renderMode = ViewModel.Settings.SubtitleRenderMode;
        var showSource = renderMode is SubtitleRenderMode.SourceOnly or SubtitleRenderMode.Dual;
        var showTranslation = renderMode is SubtitleRenderMode.TranslationOnly or SubtitleRenderMode.Dual;

        SourceSubtitleTextBlock.Visibility = showSource && !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.SourceText)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SourceSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.SourceText;

        TranslatedSubtitleTextBlock.Visibility = showTranslation && !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.TranslationText)
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranslatedSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.TranslationText;
        SubtitleOverlayBorder.Visibility = SourceSubtitleTextBlock.Visibility == Visibility.Visible || TranslatedSubtitleTextBlock.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateOverlayControlState();
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
            FullscreenControlsOverlay.Visibility = Visibility.Visible;
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
            _fullscreenControlsTimer.Stop();
            _fullscreenControlsTimer.Start();
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

            _suppressFullscreenSliderChanges = true;
            FullscreenPositionSlider.Maximum = maximumSeconds;
            FullscreenPositionSlider.Value = Math.Min(position.TotalSeconds, maximumSeconds);
            _suppressFullscreenSliderChanges = false;
        }

        UpdateScrubTimeLabels(_isPositionScrubbing ? _activeScrubber?.Value ?? position.TotalSeconds : position.TotalSeconds, maximumSeconds);
    }

    private void UpdateScrubTimeLabels(double currentSeconds, double totalSeconds)
    {
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
}
