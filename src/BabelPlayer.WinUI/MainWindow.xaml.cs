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
    private readonly SubtitleWorkflowController _subtitleWorkflowController = new();
    private readonly DispatcherTimer _transportTimer;
    private readonly IFilePickerService _filePickerService;
    private readonly WinUIWindowModeService _windowModeService;
    private IReadOnlyList<SubtitleCue> _loadedSubtitleCues = [];
    private bool _suppressPositionSliderChanges;
    private bool _isFullscreen;
    private Border AppTitleBar = null!;
    private TextBlock WindowTitleTextBlock = null!;
    private ToggleButton ThemeToggleButton = null!;
    private AppBarToggleButton BrowserPaneToggle = null!;
    private AppBarToggleButton PlaylistPaneToggle = null!;
    private InfoBar StatusInfoBar = null!;
    private Border BrowserPane = null!;
    private Border PlaylistPane = null!;
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
    private ComboBox WindowModeComboBox = null!;

    public MainShellViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        _playbackSessionController = new PlaybackSessionController(_playlistController);
        _filePickerService = new WinUIFilePickerService(this);
        _windowModeService = new WinUIWindowModeService(this);
        _transportTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };

        BuildShell();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        PlayerHost.Initialize(this);
        PlaylistList.ItemsSource = ViewModel.Playlist.Items;

        _transportTimer.Tick += TransportTimer_Tick;
        _transportTimer.Start();

        PlayerHost.MediaOpened += PlayerHost_MediaOpened;
        PlayerHost.MediaEnded += PlayerHost_MediaEnded;
        PlayerHost.MediaFailed += PlayerHost_MediaFailed;
        PlayerHost.TracksChanged += PlayerHost_TracksChanged;
        PlayerHost.RuntimeInstallProgress += PlayerHost_RuntimeInstallProgress;

        Closed += MainWindow_Closed;

        InitializeShellState();
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
        var fullscreenButton = new Button
        {
            Content = new TextBlock { Text = "Fullscreen" }
        };
        fullscreenButton.Click += ToggleFullscreen_Click;
        titleButtons.Children.Add(fullscreenButton);
        titleBarGrid.Children.Add(titleButtons);
        AppTitleBar.Child = titleBarGrid;
        RootGrid.Children.Add(AppTitleBar);

        var commandBar = new CommandBar
        {
            Margin = new Thickness(16, 12, 16, 0),
            Background = panelBrush,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right
        };
        Grid.SetRow(commandBar, 1);
        commandBar.PrimaryCommands.Add(CreatePrimaryCommand("Open", Symbol.OpenFile, OpenFile_Click));
        commandBar.PrimaryCommands.Add(CreatePrimaryCommand("Folder", Symbol.Folder, OpenFolder_Click));
        commandBar.PrimaryCommands.Add(CreatePrimaryCommand("Subtitles", Symbol.Edit, ImportSubtitle_Click));
        commandBar.PrimaryCommands.Add(new AppBarSeparator());
        commandBar.PrimaryCommands.Add(CreatePrimaryCommand("PiP", Symbol.SwitchApps, PictureInPicture_Click));
        commandBar.PrimaryCommands.Add(CreatePrimaryCommand("Borderless", Symbol.Stop, Borderless_Click));
        BrowserPaneToggle = new AppBarToggleButton { Label = "Browser Pane" };
        BrowserPaneToggle.Click += BrowserPaneToggle_Click;
        PlaylistPaneToggle = new AppBarToggleButton { Label = "Playlist Pane" };
        PlaylistPaneToggle.Click += PlaylistPaneToggle_Click;
        commandBar.SecondaryCommands.Add(BrowserPaneToggle);
        commandBar.SecondaryCommands.Add(PlaylistPaneToggle);
        commandBar.SecondaryCommands.Add(new AppBarButton { Label = "Reset Window" });
        ((AppBarButton)commandBar.SecondaryCommands[^1]).Click += StandardWindow_Click;
        commandBar.SecondaryCommands.Add(new AppBarButton { Label = "Add Videos Root" });
        ((AppBarButton)commandBar.SecondaryCommands[^1]).Click += AddRootFolder_Click;
        RootGrid.Children.Add(commandBar);

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

        var contentGrid = new Grid
        {
            Padding = new Thickness(16, 12, 16, 12),
            ColumnSpacing = 16
        };
        Grid.SetRow(contentGrid, 3);
        BrowserColumn = new ColumnDefinition { Width = new GridLength(280) };
        PlaylistColumn = new ColumnDefinition { Width = new GridLength(320) };
        contentGrid.ColumnDefinitions.Add(BrowserColumn);
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(PlaylistColumn);
        RootGrid.Children.Add(contentGrid);

        BrowserPane = CreatePanelBorder(panelBrush, borderBrush);
        Grid.SetColumn(BrowserPane, 0);
        contentGrid.Children.Add(BrowserPane);
        BrowserPane.Child = BuildBrowserPane();

        var playerPane = CreatePanelBorder(panelBrush, borderBrush);
        Grid.SetColumn(playerPane, 1);
        contentGrid.Children.Add(playerPane);
        playerPane.Child = BuildPlayerPane(accentBrush);

        PlaylistPane = CreatePanelBorder(panelBrush, borderBrush);
        Grid.SetColumn(PlaylistPane, 2);
        contentGrid.Children.Add(PlaylistPane);
        PlaylistPane.Child = BuildPlaylistPane();

        var transportBorder = CreatePanelBorder(panelBrush, borderBrush);
        transportBorder.Margin = new Thickness(16, 0, 16, 16);
        transportBorder.Padding = new Thickness(16, 14, 16, 14);
        Grid.SetRow(transportBorder, 4);
        transportBorder.Child = BuildTransportPane();
        RootGrid.Children.Add(transportBorder);
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

        PlayerHost = new MpvHostControl();
        grid.Children.Add(PlayerHost);

        var decoderBadge = new Border
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
        decoderBadge.Child = decoderStack;
        grid.Children.Add(decoderBadge);

        var subtitleBorder = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24),
            Padding = new Thickness(18, 14, 18, 14),
            CornerRadius = new CornerRadius(22),
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
        subtitleBorder.Child = subtitleStack;
        grid.Children.Add(subtitleBorder);

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

        WindowModeComboBox = new ComboBox { Header = "Window Mode" };
        WindowModeComboBox.Items.Add("Standard");
        WindowModeComboBox.Items.Add("Borderless");
        WindowModeComboBox.Items.Add("Picture in Picture");
        WindowModeComboBox.SelectionChanged += WindowModeComboBox_SelectionChanged;
        Grid.SetRow(WindowModeComboBox, 1);
        Grid.SetColumn(WindowModeComboBox, 2);
        Grid.SetColumnSpan(WindowModeComboBox, 2);
        grid.Children.Add(WindowModeComboBox);

        var hintText = new TextBlock
        {
            Text = "Drag local video files or folders anywhere into the window to queue them.",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 205, 219))
        };
        Grid.SetRow(hintText, 1);
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
        ViewModel.SubtitleOverlay.ShowSource = settings.SubtitleRenderMode is SubtitleRenderMode.SourceOnly or SubtitleRenderMode.Dual;
        ViewModel.SubtitleOverlay.TranslationText = "Drop a file or choose Open to start playback.";
        WindowTitleTextBlock.Text = ViewModel.WindowTitle;
        TranslatedSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.TranslationText;
        StatusInfoBar.IsOpen = true;
        StatusInfoBar.Message = ViewModel.StatusMessage;

        ThemeToggleButton.IsChecked = true;
        ApplyTheme(isDark: true);
        VolumeSlider.Value = ViewModel.Transport.Volume;
        SpeedComboBox.SelectedIndex = 1;
        WindowModeComboBox.SelectedIndex = settings.WindowMode switch
        {
            PlaybackWindowMode.Borderless => 1,
            PlaybackWindowMode.PictureInPicture => 2,
            _ => 0
        };
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
                _windowModeService.CurrentMode),
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
            _loadedSubtitleCues = await _subtitleWorkflowController.LoadExternalSubtitleCuesAsync(
                subtitlePath,
                progress => ShowStatus($"Subtitle runtime: {progress.Stage}"),
                message => ShowStatus(message),
                CancellationToken.None);

            ViewModel.SubtitleOverlay.ShowSource = true;
            ViewModel.SubtitleOverlay.SourceText = _loadedSubtitleCues.FirstOrDefault()?.SourceText ?? string.Empty;
            ViewModel.SubtitleOverlay.TranslationText = _loadedSubtitleCues.FirstOrDefault()?.TranslatedText ?? _loadedSubtitleCues.FirstOrDefault()?.SourceText ?? "Subtitles imported.";
            UpdateSubtitleVisibility();
            ShowStatus($"Imported {Path.GetFileName(subtitlePath)} with {_loadedSubtitleCues.Count} cues.");
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, true);
        }
    }

    private async void PictureInPicture_Click(object sender, RoutedEventArgs e)
    {
        await _windowModeService.SetModeAsync(PlaybackWindowMode.PictureInPicture);
        WindowModeComboBox.SelectedIndex = 2;
        ShowStatus("Picture in picture mode enabled.");
    }

    private async void Borderless_Click(object sender, RoutedEventArgs e)
    {
        await _windowModeService.SetModeAsync(PlaybackWindowMode.Borderless);
        WindowModeComboBox.SelectedIndex = 1;
        ShowStatus("Borderless mode enabled.");
    }

    private async void StandardWindow_Click(object sender, RoutedEventArgs e)
    {
        await _windowModeService.SetModeAsync(PlaybackWindowMode.Standard);
        WindowModeComboBox.SelectedIndex = 0;
        ShowStatus("Standard window mode restored.");
    }

    private async void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            await _windowModeService.ExitFullscreenAsync();
            _isFullscreen = false;
            ShowStatus("Fullscreen exited.");
            return;
        }

        await _windowModeService.EnterFullscreenAsync();
        _isFullscreen = true;
        ShowStatus("Fullscreen enabled.");
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

    private async void WindowModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowModeComboBox.SelectedIndex < 0)
        {
            return;
        }

        var mode = WindowModeComboBox.SelectedIndex switch
        {
            1 => PlaybackWindowMode.Borderless,
            2 => PlaybackWindowMode.PictureInPicture,
            _ => PlaybackWindowMode.Standard
        };

        await _windowModeService.SetModeAsync(mode);
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

        _suppressPositionSliderChanges = true;
        PositionSlider.Maximum = Math.Max(duration.TotalSeconds, 1);
        PositionSlider.Value = Math.Min(position.TotalSeconds, PositionSlider.Maximum);
        _suppressPositionSliderChanges = false;

        PlayPauseButton.Content = ViewModel.Transport.IsPaused ? "Play" : "Pause";
        TimeTextBlock.Text = $"{ViewModel.Transport.CurrentTimeText} / {ViewModel.Transport.DurationText}";
        MuteToggleButton.IsChecked = ViewModel.Transport.IsMuted;

        UpdateSubtitleOverlay(position);
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
        PlayerHost.Volume = ViewModel.Transport.Volume;
        await Task.CompletedTask;
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
        var showBrowser = ViewModel.Browser.IsVisible && width >= 900;
        var showPlaylist = ViewModel.Playlist.IsVisible && width >= 1160;

        BrowserPane.Visibility = showBrowser ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPane.Visibility = showPlaylist ? Visibility.Visible : Visibility.Collapsed;
        BrowserColumn.Width = showBrowser ? new GridLength(280) : new GridLength(0);
        PlaylistColumn.Width = showPlaylist ? new GridLength(320) : new GridLength(0);
        BrowserPaneToggle.IsChecked = ViewModel.Browser.IsVisible;
        PlaylistPaneToggle.IsChecked = ViewModel.Playlist.IsVisible;
    }

    private void UpdateSubtitleOverlay(TimeSpan position)
    {
        if (_loadedSubtitleCues.Count == 0)
        {
            UpdateSubtitleVisibility();
            return;
        }

        var currentCue = _loadedSubtitleCues.FirstOrDefault(cue => position >= cue.Start && position <= cue.End);
        if (currentCue is null)
        {
            ViewModel.SubtitleOverlay.SourceText = string.Empty;
            ViewModel.SubtitleOverlay.TranslationText = string.Empty;
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
        SourceSubtitleTextBlock.Visibility = ViewModel.SubtitleOverlay.ShowSource && !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.SourceText)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SourceSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.SourceText;

        TranslatedSubtitleTextBlock.Visibility = !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.TranslationText)
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranslatedSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.TranslationText;
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
