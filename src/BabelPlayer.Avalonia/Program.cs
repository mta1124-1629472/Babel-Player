using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BabelPlayer.Avalonia;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ValidateNativeDependencies();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void ValidateNativeDependencies()
    {
        const string LibMpv = "libmpv-2.dll";
        var path = Path.Combine(AppContext.BaseDirectory, LibMpv);
        if (!File.Exists(path))
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            var message =
                $"Cannot start BabelPlayer: required native library is missing.\n\n" +
                $"Expected: {path}\n\n" +
                $"Re-run the installer or place the {arch} build of {LibMpv} next to the executable.";
            MessageBoxW(IntPtr.Zero, message, "BabelPlayer – Missing Dependency", 0x10 /* MB_ICONERROR | MB_OK */);
            Environment.Exit(1);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
