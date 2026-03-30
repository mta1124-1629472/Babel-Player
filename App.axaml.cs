using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Babel.Player.Services;
using Babel.Player.ViewModels;
using Babel.Player.Views;

namespace Babel.Player;

public partial class App : Application
{
    private SessionWorkflowCoordinator? _sessionWorkflowCoordinator;
    private AppLog? _startupLog;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Register global exception handlers before doing anything else.
        // These log the crash cause and do NOT restart the app.
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BabelPlayer");

            var appLog = new AppLog(Path.Combine(appDataRoot, "logs", "babel-player.log"));
            _startupLog = appLog;

            try
            {
                appLog.Info("App startup: initializing session coordinator.");
                var store = new SessionSnapshotStore(Path.Combine(appDataRoot, "state", "current-session.json"), appLog);
                _sessionWorkflowCoordinator = new SessionWorkflowCoordinator(store, appLog);
                _sessionWorkflowCoordinator.Initialize();
                appLog.Info("App startup: session coordinator ready.");
            }
            catch (Exception ex)
            {
                // Session restore failed — log it and continue with a degraded coordinator.
                // The app still opens; the user sees an empty session rather than a crash.
                _startupLog?.Error("App startup: session initialization failed. Continuing with empty session.", ex);
                if (_sessionWorkflowCoordinator is null)
                {
                    var fallbackDataRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BabelPlayer");
                    var fallbackLog = _startupLog ?? new AppLog(Path.Combine(fallbackDataRoot, "logs", "babel-player.log"));
                    var fallbackStore = new SessionSnapshotStore(
                        Path.Combine(fallbackDataRoot, "state", "current-session.json"), fallbackLog);
                    _sessionWorkflowCoordinator = new SessionWorkflowCoordinator(fallbackStore, fallbackLog);
                }
            }

            desktop.Exit += OnDesktopExit;

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(_sessionWorkflowCoordinator),
            };
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
        e.SetObserved(); // prevent process crash from unobserved async exceptions
    }
}