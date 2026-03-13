using Microsoft.UI.Xaml;

namespace BabelPlayer.WinUI;

public partial class App : Application
{
    private Window? _window;
    private readonly IAppTelemetryBootstrap _telemetry;
    private readonly IBabelLogger _logger;

    public App()
    {
        _telemetry = new AppTelemetryBootstrap();
        _logger = _telemetry.LogFactory.CreateLogger("app");
        InitializeComponent();
        RegisterGlobalExceptionHandlers();
        _logger.LogInfo("Application constructed.");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _logger.LogInfo("Application launch starting.");
        try
        {
            _window = new MainWindow(new ShellCompositionRoot(_telemetry));
            _window.Closed += Window_Closed;
            _window.Activate();
            _logger.LogInfo("Application launch completed.");
        }
        catch (Exception ex)
        {
            _telemetry.WriteUnhandledException("app.launch", ex);
            _telemetry.Flush(TimeSpan.FromSeconds(1));
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
        _telemetry.Flush(TimeSpan.FromSeconds(2));
        _telemetry.Dispose();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _telemetry.WriteUnhandledException(
            "app.unhandled.ui",
            e.Exception,
            BabelLogContext.Create(("handled", e.Handled)));
        _telemetry.Flush(TimeSpan.FromSeconds(1));
    }

    private void CurrentDomain_UnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _telemetry.WriteUnhandledException(
                "app.unhandled.domain",
                exception,
                BabelLogContext.Create(("isTerminating", e.IsTerminating)));
        }

        _telemetry.Flush(TimeSpan.FromSeconds(1));
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _telemetry.WriteUnhandledException("app.unobserved-task", e.Exception);
        _telemetry.Flush(TimeSpan.FromSeconds(1));
        e.SetObserved();
    }
}
