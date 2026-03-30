using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.IO;

namespace SqlAgMonitor;

sealed class Program
{
    public static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SqlAgMonitor", "logs");

    public static string LogFilePath { get; private set; } = string.Empty;

    [STAThread]
    public static void Main(string[] args)
    {
        Directory.CreateDirectory(LogDir);
        LogFilePath = Path.Combine(LogDir, $"agmonitor-{DateTime.Now:yyyyMMdd}.log");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteLog("FATAL", $"Unhandled exception: {e.ExceptionObject}");
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteLog("FATAL", $"Startup crash: {ex}");
            throw;
        }
    }

    public static void WriteLog(string level, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, line);
        }
        catch { /* last resort — can't log the logging failure */ }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
