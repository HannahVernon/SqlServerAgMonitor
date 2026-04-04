using Avalonia;
using Avalonia.ReactiveUI;
using ReactiveUI;
using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Threading.Tasks;

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

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteLog("ERROR", $"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        RxApp.DefaultExceptionHandler = new GlobalRxExceptionHandler();

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

    /// <summary>
    /// Prevents unhandled exceptions in ReactiveUI observable pipelines
    /// from tearing down the process. Logs the error and swallows it so
    /// the application stays alive.
    /// </summary>
    private sealed class GlobalRxExceptionHandler : IObserver<Exception>
    {
        public void OnNext(Exception value)
        {
            WriteLog("ERROR", $"ReactiveUI unhandled exception: {value}");
        }

        public void OnError(Exception error)
        {
            WriteLog("FATAL", $"ReactiveUI exception handler error: {error}");
        }

        public void OnCompleted() { }
    }
}
