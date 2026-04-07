using Avalonia;
using Babel.Player.Services;
using System;
using System.Runtime.InteropServices;

namespace Babel.Player;

sealed class Program
{
    // AttachConsole lets WinExe binaries write to the parent terminal.
    // Returns false (harmlessly) when there is no parent console to attach to.
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    // [STAThread] is honoured only on synchronous entry points.
    // The benchmark path is headless; .GetAwaiter().GetResult() is safe there.
    [STAThread]
    public static int Main(string[] args)
    {
        // Intercept --benchmark before Avalonia sees the args.
        // All other args are forwarded to Avalonia as normal.
        if (Array.Exists(args, a =>
                string.Equals(a, "--benchmark", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(-1); // attach to parent terminal; no-op if none exists
            return BenchmarkCli.RunAsync(args).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
#if DEBUG
            .LogToTrace();
#else
            ;
#endif
}
