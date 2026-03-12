using BabelPlayer.Core;
using Microsoft.UI.Xaml;

namespace BabelPlayer.WinUI;

public partial class App : Application
{
    private Window? _window;
    private readonly BabelLogManager _logManager;
    private readonly AppDiagnosticsContext _diagnosticsContext;
    private readonly IBabelLogger _logger;

    public App()
    {
        _logManager = new BabelLogManager(BabelLogOptions.CreateDefault());
        _diagnosticsContext = new AppDiagnosticsContext();
        _logger = _logManager.CreateLogger("app");
        InitializeComponent();
        RegisterGlobalExceptionHandlers();
        _logger.LogInfo("Application constructed.");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _logger.LogInfo("Application launch starting.");
        try
        {
            _window = new MainWindow(new ShellCompositionRoot(_logManager, _diagnosticsContext));
            _window.Closed += Window_Closed;
            _window.Activate();
            _logger.LogInfo("Application launch completed.");
        }
        catch (Exception ex)
        {
            _logManager.WriteUnhandledException("app.launch", ex, _diagnosticsContext.Snapshot);
            _logManager.Flush(TimeSpan.FromSeconds(1));
            throw;
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _logger.LogInfo("Application shutdown starting.");
        _logManager.Flush(TimeSpan.FromSeconds(2));
        _logManager.Dispose();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logManager.WriteUnhandledException(
            "app.unhandled.ui",
            e.Exception,
            _diagnosticsContext.Snapshot,
            BabelLogContext.Create(("handled", e.Handled)));
        _logManager.Flush(TimeSpan.FromSeconds(1));
    }

    private void CurrentDomain_UnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _logManager.WriteUnhandledException(
                "app.unhandled.domain",
                exception,
                _diagnosticsContext.Snapshot,
                BabelLogContext.Create(("isTerminating", e.IsTerminating)));
        }

        _logManager.Flush(TimeSpan.FromSeconds(1));
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logManager.WriteUnhandledException(
            "app.unobserved-task",
            e.Exception,
            _diagnosticsContext.Snapshot);
        _logManager.Flush(TimeSpan.FromSeconds(1));
        e.SetObserved();
    }
}
