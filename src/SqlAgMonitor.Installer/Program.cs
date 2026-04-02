using System;
using Avalonia;
using Avalonia.ReactiveUI;

namespace SqlAgMonitor.Installer;

internal sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Check for /uninstall flag
        if (args.Length > 0 && args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return UninstallHandler.Run();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI()
            .LogToTrace();
}
