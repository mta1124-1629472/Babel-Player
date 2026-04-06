using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;
using Babel.Player.Views;

namespace Babel.Player;

public partial class App : Application
{
    private SessionWorkflowCoordinator? _sessionWorkflowCoordinator;
    private AppLog? _startupLog;
    private SettingsService? _settingsService;
    private ApiKeyStore? _apiKeyStore;

    // Resolved once at startup so crash handlers can reference it without
    // touching the AppLog instance (which may itself be in a bad state).
    private string? _logFilePath;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BabelPlayer");

            _logFilePath = Path.Combine(appDataRoot, "logs", "babel-player.log");
            var appLog = new AppLog(_logFilePath);
            _startupLog = appLog;

            // Initialize Settings and other stores
            var settingsFilePath = Path.Combine(appDataRoot, "settings", "app-settings.json");
            _settingsService = new SettingsService(settingsFilePath, appLog);
            var appSettings = _settingsService.LoadOrDefault();
            if (!string.Equals(appSettings.EffectiveContainerizedServiceUrl, appSettings.ContainerizedServiceUrl, StringComparison.Ordinal))
            {
                appLog.Info(
                    $"Environment override active: {AppSettings.InferenceServiceUrlEnvVar}={appSettings.EffectiveContainerizedServiceUrl}");
            }

            var perSessionStore = new PerSessionSnapshotStore(
                Path.Combine(appDataRoot, "sessions"), appLog);
            var recentStore = new RecentSessionsStore(
                Path.Combine(appDataRoot, "state", "recent-sessions.json"), appLog);

            var legacyKeyPath = Path.Combine(appDataRoot, "state", "api-keys.json");
            ISecureCredentialProvider keyProvider = OperatingSystem.IsWindows()
                ? new WindowsCredentialProvider()
                : new FileSystemCredentialProvider(legacyKeyPath);

            _apiKeyStore = new ApiKeyStore(keyProvider, legacyKeyPath);
            var modelDownloader = new ModelDownloader(appLog);
            var transportManager = new MediaTransportManager(
                videoOptions: new VideoPlaybackOptions(
                    HwdecMode:      appSettings.VideoHwdec,
                    GpuApi:         appSettings.VideoGpuApi,
                    UseGpuNext:     appSettings.VideoUseGpuNext,
                    VsrEnabled:     appSettings.VideoVsrEnabled,
                    VsrQuality:     appSettings.VideoVsrQuality,
                    HdrEnabled:     appSettings.VideoHdrEnabled,
                    ToneMapping:    appSettings.VideoToneMapping,
                    TargetPeak:     appSettings.VideoTargetPeak,
                    HdrComputePeak: appSettings.VideoHdrComputePeak),
                log: appLog);

            try
            {
                appLog.Info("App startup: initializing session coordinator.");
                var containerizedProbe = new ContainerizedServiceProbe(appLog);
                var managedHostManager = new ManagedVenvHostManager(appLog, containerizedProbe);
                var dockerHostManager = new ContainerizedInferenceManager(appLog, containerizedProbe);
                var containerizedManager = new CompositeInferenceHostManager(managedHostManager, dockerHostManager);
                var transcriptionRegistry = new TranscriptionRegistry(appLog, containerizedProbe);
                var translationRegistry = new TranslationRegistry(appLog, containerizedProbe);
                var ttsRegistry = new TtsRegistry(appLog, containerizedProbe);
                var store = new SessionSnapshotStore(Path.Combine(appDataRoot, "state", "current-session.json"), appLog);
                _sessionWorkflowCoordinator = new SessionWorkflowCoordinator(
                    store, appLog, appSettings, perSessionStore, recentStore, transcriptionRegistry, translationRegistry, ttsRegistry, transportManager: transportManager, keyStore: _apiKeyStore, containerizedProbe: containerizedProbe, containerizedInferenceManager: containerizedManager);
                _sessionWorkflowCoordinator.Initialize();
                containerizedManager.RequestEnsureStarted(appSettings, ContainerizedStartupTrigger.AppStartup);
                appLog.Info("App startup: session coordinator ready.");
            }
            catch (Exception ex)
            {
                _startupLog?.Error("App startup: session initialization failed. Continuing with empty session.", ex);
                if (_sessionWorkflowCoordinator is null)
                {
                    var containerizedProbe = new ContainerizedServiceProbe(appLog);
                    var managedHostManager = new ManagedVenvHostManager(appLog, containerizedProbe);
                    var dockerHostManager = new ContainerizedInferenceManager(appLog, containerizedProbe);
                    var containerizedManager = new CompositeInferenceHostManager(managedHostManager, dockerHostManager);
                    var transcriptionRegistry = new TranscriptionRegistry(appLog, containerizedProbe);
                    var translationRegistry = new TranslationRegistry(appLog, containerizedProbe);
                    var ttsRegistry = new TtsRegistry(appLog, containerizedProbe);
                    var fallbackStore = new SessionSnapshotStore(
                        Path.Combine(appDataRoot, "state", "current-session.json"), appLog);
                    _sessionWorkflowCoordinator = new SessionWorkflowCoordinator(
                        fallbackStore, appLog, appSettings, perSessionStore, recentStore, transcriptionRegistry, translationRegistry, ttsRegistry, transportManager: transportManager, keyStore: _apiKeyStore, containerizedProbe: containerizedProbe, containerizedInferenceManager: containerizedManager);
                    containerizedManager.RequestEnsureStarted(appSettings, ContainerizedStartupTrigger.AppStartup);
                }
            }

            desktop.Exit += OnDesktopExit;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            var errorDialogService = new AvaloniaErrorDialogService(appLog);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(
                    _sessionWorkflowCoordinator,
                    _settingsService,
                    modelDownloader,
                    _apiKeyStore,
                    errorDialogService,
                    logFilePath: _logFilePath),
            };

            // Run heavy startup probes in background and publish results on UI thread.
            var coordinator = _sessionWorkflowCoordinator;
            Task.Run(() => coordinator.GatherBootstrapWarmupData())
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        Dispatcher.UIThread.Post(() => coordinator.ApplyBootstrapWarmupData(t.Result));
                    }
                    else if (t.Exception is not null)
                    {
                        _startupLog?.Error("Background bootstrap warmup failed.", t.Exception.Flatten());
                    }
                });

            // Detect hardware in background; post result to UI thread when done.
            Task.Run(() => HardwareSnapshot.Run())
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        Dispatcher.UIThread.Post(() => coordinator.HardwareSnapshot = t.Result);
                });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_sessionWorkflowCoordinator is null) return;
        try
        {
            _sessionWorkflowCoordinator.FlushPendingSave();
        }
        catch (Exception ex)
        {
            _startupLog?.Error("Failed to save session on exit.", ex);
        }
        finally
        {
            _sessionWorkflowCoordinator.Dispose();
            (_startupLog as IDisposable)?.Dispose();

            // Force the process to exit cleanly. Without this, background threads
            // (mpv event loop, debounce Task.Run continuations, bootstrap warmup)
            // can keep the CLR alive indefinitely after the window has closed.
            Environment.Exit(e.ApplicationExitCode);
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var msg = e.ExceptionObject is Exception ex ? ex.ToString() : e.ExceptionObject?.ToString() ?? "unknown";

        // 1. Always log to disk first — this is guaranteed to run even if the
        //    UI thread is in a bad state.
        _startupLog?.Error($"Unhandled exception (isTerminating={e.IsTerminating}).",
            new InvalidOperationException(msg));

        // 2. Show the full error to the user in a dedicated pop-up window.
        var header = e.IsTerminating
            ? $"FATAL — application will close after this dialog.\n\n{msg}"
            : msg;
        CrashReportWindow.ShowOnUiThread(header, _logFilePath);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // 1. Mark observed so the runtime does not re-throw and terminate.
        e.SetObserved();

        var msg = e.Exception.ToString();

        // 2. Log to disk.
        _startupLog?.Error("Unobserved task exception.", e.Exception);

        // 3. Show full error to the user.
        CrashReportWindow.ShowOnUiThread(msg, _logFilePath);
    }
}
