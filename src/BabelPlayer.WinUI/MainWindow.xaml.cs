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
    private readonly IPlaybackHostRuntime _playbackHostRuntime;
    private readonly IShellPreferencesService _shellPreferencesService;
    private readonly IShellPreferenceCommands _shellPreferenceCommands;
    private readonly IShellLibraryService _shellLibraryService;
    private readonly IShellProjectionReader _shellProjectionReader;
    private readonly IQueueProjectionReader _queueProjectionReader;
    private readonly IQueueCommands _queueCommands;
    private readonly IShellPlaybackCommands _shellPlaybackCommands;
    private readonly ICredentialSetupService _credentialSetupService;
    private readonly IShortcutProfileService _shortcutProfileService;
    private readonly IShortcutCommandExecutor _shortcutCommandExecutor;
    private readonly IDisposable _shellLifetime;
    private readonly IVideoPresenter _videoPresenter;
    private readonly ISubtitlePresenter _subtitlePresenter;
    private readonly IFilePickerService _filePickerService;
    private readonly WinUIWindowModeService _windowModeService;
    private readonly ICredentialDialogService _credentialDialogService;
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
        _diagnosticsContext = dependencies.DiagnosticsContext;
        _logger = dependencies.LogFactory.CreateLogger("shell.window");
        _statusLogger = dependencies.LogFactory.CreateLogger("shell.status");
        _subtitleWorkflowService = dependencies.SubtitleWorkflowService;
        _playbackHostRuntime = dependencies.PlaybackHostRuntime;
        _videoPresenter = dependencies.VideoPresenter;
        _subtitlePresenter = dependencies.SubtitlePresenter;
        _shellPreferencesService = dependencies.ShellPreferencesService;
        _shellPreferenceCommands = dependencies.ShellPreferenceCommands;
        _shellLibraryService = dependencies.ShellLibraryService;
        _shellProjectionReader = dependencies.ShellProjectionReader;
        _queueProjectionReader = dependencies.QueueProjectionReader;
        _queueCommands = dependencies.QueueCommands;
        _shellPlaybackCommands = dependencies.ShellPlaybackCommands;
        _credentialSetupService = dependencies.CredentialSetupService;
        _shortcutProfileService = dependencies.ShortcutProfileService;
        _shortcutCommandExecutor = dependencies.ShortcutCommandExecutor;
        _shellLifetime = dependencies.ShellLifetime;
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
        _shellProjectionReader.ProjectionChanged += ShellProjectionService_ProjectionChanged;
        _queueProjectionReader.QueueSnapshotChanged += ShellController_QueueSnapshotChanged;

        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;

        InitializeShellState();
        _diagnosticsContext.UpdateWindowMode(_windowModeService.CurrentMode.ToString());
        ApplyShellProjection(_shellProjectionReader.Current);
        ApplyQueueSnapshot(_queueProjectionReader.QueueSnapshot);
        _logger.LogInfo("Main window initialized.");
        FireAndForget(_subtitleWorkflowService.InitializeAsync());
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
        _stageCoordinator.Dispose();
        _subtitleWorkflowService.StatusChanged -= SubtitleWorkflow_StatusChanged;
        _subtitleWorkflowService.SnapshotChanged -= SubtitleWorkflow_SnapshotChanged;
        _shellLibraryService.SnapshotChanged -= ShellLibraryService_SnapshotChanged;
        _shellProjectionReader.ProjectionChanged -= ShellProjectionService_ProjectionChanged;
        _queueProjectionReader.QueueSnapshotChanged -= ShellController_QueueSnapshotChanged;
        (_subtitlePresenter as IDisposable)?.Dispose();
        _shellLifetime.Dispose();
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
        ApplyPreferencesSnapshot(_shellPreferenceCommands.ApplyLayoutChange(new ShellLayoutPreferencesChange(
            ViewModel.Browser.IsVisible,
            ViewModel.Queue.IsVisible,
            _windowModeService.CurrentMode)));
    }

    private async Task ApplyAudioStatePreferenceAsync(double volume, bool muted)
    {
        var snapshot = await _shellPreferenceCommands.ApplyAudioStateAsync(
            new ShellAudioStateChange(volume, muted));
        DispatcherQueue.TryEnqueue(() => ApplyPreferencesSnapshot(snapshot));
    }

    private async Task ApplyPlaybackDefaultsPreferenceAsync(ShellPlaybackDefaultsChange change)
    {
        var snapshot = await _shellPreferenceCommands.ApplyPlaybackDefaultsAsync(change);
        DispatcherQueue.TryEnqueue(() => ApplyPreferencesSnapshot(snapshot));
    }

    private void PersistPlaybackDefaults()
    {
        FireAndForget(ApplyPlaybackDefaultsPreferenceAsync(new ShellPlaybackDefaultsChange(
            ViewModel.Settings.HardwareDecodingMode,
            ViewModel.Transport.PlaybackRate,
            ViewModel.Settings.AudioDelaySeconds,
            ViewModel.Settings.SubtitleDelaySeconds,
            ViewModel.Settings.AspectRatio)));
    }

    private void PersistSubtitlePresentationPreferences()
    {
        ApplyPreferencesSnapshot(_shellPreferenceCommands.ApplySubtitlePresentationChange(new ShellSubtitlePresentationChange(
            ViewModel.Settings.SubtitleRenderMode,
            ViewModel.Settings.SubtitleStyle)));
    }

    private void PersistAudioStatePreferences()
    {
        var volume = Math.Clamp((VolumeSlider?.Value ?? (ViewModel.Transport.Volume * 100d)) / 100d, 0, 1);
        FireAndForget(ApplyAudioStatePreferenceAsync(volume, MuteToggleButton?.IsChecked == true));
    }

    private void PersistShortcutProfile(ShortcutProfile profile)
    {
        ApplyPreferencesSnapshot(_shellPreferenceCommands.ApplyShortcutProfileChange(profile));
    }

    private void PersistResumeEnabledPreference(bool resumeEnabled)
    {
        ApplyPreferencesSnapshot(_shellPreferenceCommands.ApplyResumeEnabledChange(resumeEnabled).UpdatedPreferences);
    }

}
