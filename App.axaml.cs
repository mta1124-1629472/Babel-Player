using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
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

    /// <summary>
    /// Loads the Avalonia XAML resources for the application.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Performs global application initialization: configures logging, settings, secure credential storage, media/transport components, the session workflow coordinator, the main window and UI theme, global crash handlers, and background startup probes.
    /// </summary>
    /// <remarks>
    /// Side effects:
    /// - Subscribes to AppDomain.CurrentDomain.UnhandledException and TaskScheduler.UnobservedTaskException.
    /// - Initializes application data directories, log file, and SettingsService.
    /// - Chooses and configures an ISecureCredentialProvider and creates the ApiKeyStore.
    /// - Creates media/transport components and the session workflow coordinator via DependencyLocator.
    /// - Creates and shows the main window with its view model and sets application shutdown behavior.
    /// - Wires GPU bootstrap progress into the UI status bar (debounced) when a primary GPU manager is available.
    /// - Starts background tasks to gather bootstrap warmup data and detect hardware, posting results to the UI thread.
    /// <summary>
    /// Performs application startup: sets up global exception handlers and logging, loads settings and persistence stores, initializes credential and media subsystems, constructs the session coordinator and main window view model, wires desktop lifecycle handlers and UI status updates, and starts background warmup and hardware-detection probes.
    /// </summary>
    /// <remarks>
    /// Executed after the Avalonia framework has initialized. Initialization is performed only for a classic desktop lifetime; otherwise control falls through to the base implementation. This method also forces the app theme to dark and registers an exit handler to flush and dispose session resources on shutdown.
    /// </remarks>
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

            // Apply saved theme preference — forced to Dark in App.axaml
            if (Application.Current is { } app)
            {
                app.RequestedThemeVariant = ThemeVariant.Dark;
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
                videoOptionsFactory: () => new VideoPlaybackOptions(
                    HwdecMode:      appSettings.VideoHwdec,
                    GpuApi:         appSettings.VideoGpuApi,
                    UseGpuNext:     appSettings.VideoUseGpuNext,
                    VsrEnabled:     appSettings.VideoVsrEnabled,
                    HdrEnabled:     appSettings.VideoHdrEnabled,
                    AllowHdrPassthrough: appSettings.VideoHdrEnabled && HardwareSnapshot.QueryActiveHdrDisplay(),
                    ToneMapping:    appSettings.VideoToneMapping,
                    TargetPeak:     appSettings.VideoTargetPeak,
                    HdrComputePeak: appSettings.VideoHdrComputePeak),
                log: appLog);
            _sessionWorkflowCoordinator = DependencyLocator.CreateSessionCoordinator(
                appLog, appSettings, perSessionStore, recentStore, _apiKeyStore, transportManager, 
                appDataRoot, _startupLog, out var primaryGpuManager);


            desktop.Exit += OnDesktopExit;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            var errorDialogService = new AvaloniaErrorDialogService(appLog);

            var mainVm = new MainWindowViewModel(
                _sessionWorkflowCoordinator,
                _settingsService,
                modelDownloader,
                _apiKeyStore,
                errorDialogService,
                logFilePath: _logFilePath);

            desktop.MainWindow = new MainWindow { DataContext = mainVm };

            // Wire live bootstrap progress into the status bar.
            // Debounce: create a new 150 ms one-shot timer on each line; swapping
            // it atomically disposes the previous pending update so rapid pip/uv
            // output doesn't flood the dispatcher queue.
            System.Threading.Timer? statusDebounce = null;
            void PostStatus(string line)
            {
                var captured = line;
                var timer = new System.Threading.Timer(
                    _ => Dispatcher.UIThread.Post(() => mainVm.Playback.StatusText = $"Setup: {captured}"),
                    null, 150, System.Threading.Timeout.Infinite);
                System.Threading.Interlocked.Exchange(ref statusDebounce, timer)?.Dispose();
            }

            // GPU venv — surface install progress (first-time and rebuilds).
            if (primaryGpuManager is not null)
                primaryGpuManager.BootstrapProgressCallback = PostStatus;

            // CPU runtime installation is intentionally not triggered during startup.
            // Defer any first-time bootstrap/download until a user-initiated CPU
            // transcription/TTS workflow explicitly requests it.

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
