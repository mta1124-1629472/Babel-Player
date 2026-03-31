using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
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

            var appLog = new AppLog(Path.Combine(appDataRoot, "logs", "babel-player.log"));
            _startupLog = appLog;
            // Initialize Settings and other stores
            var settingsFilePath = Path.Combine(appDataRoot, "settings", "app-settings.json");
            _settingsService = new SettingsService(settingsFilePath, appLog);
            var appSettings = _settingsService.LoadOrDefault();

            var perSessionStore = new PerSessionSnapshotStore(
                Path.Combine(appDataRoot, "sessions"), appLog);
            var recentStore = new RecentSessionsStore(
                Path.Combine(appDataRoot, "state", "recent-sessions.json"), appLog);

            _apiKeyStore = new ApiKeyStore(appDataRoot);

            try
            {
                appLog.Info("App startup: initializing session coordinator.");
                var store = new SessionSnapshotStore(Path.Combine(appDataRoot, "state", "current-session.json"), appLog);
                _sessionWorkflowCoordinator = new SessionWorkflowCoordinator(
                    store, appLog, appSettings, perSessionStore, recentStore, keyStore: _apiKeyStore);
                _sessionWorkflowCoordinator.Initialize();
                appLog.Info("App startup: session coordinator ready.");
            }
            catch (Exception ex)
            {
                _startupLog?.Error("App startup: session initialization failed. Continuing with empty session.", ex);
                if (_sessionWorkflowCoordinator is null)
                {
                    var fallbackStore = new SessionSnapshotStore(
                        Path.Combine(appDataRoot, "state", "current-session.json"), appLog);
                    _sessionWorkflowCoordinator = new SessionWorkflowCoordinator(
                        fallbackStore, appLog, appSettings, perSessionStore, recentStore, keyStore: _apiKeyStore);
                }
            }

            desktop.Exit += OnDesktopExit;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(_sessionWorkflowCoordinator, _settingsService, _apiKeyStore),
            };
            // Detect hardware in background; post result to UI thread when done
            var coordinator = _sessionWorkflowCoordinator;
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
            _sessionWorkflowCoordinator.SaveCurrentSession();
        }
        catch (Exception ex)
        {
            _startupLog?.Error("Failed to save session on exit.", ex);
        }
        finally
        {
            _sessionWorkflowCoordinator.Dispose();
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var msg = e.ExceptionObject is Exception ex ? ex.ToString() : e.ExceptionObject?.ToString() ?? "unknown";
        _startupLog?.Error($"Unhandled exception (isTerminating={e.IsTerminating}).", new InvalidOperationException(msg));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _startupLog?.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}