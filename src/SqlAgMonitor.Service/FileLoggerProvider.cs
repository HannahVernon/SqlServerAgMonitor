using Microsoft.Extensions.Logging;

namespace SqlAgMonitor.Service;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;

    public FileLoggerProvider(string filePath) => _filePath = filePath;

    public ILogger CreateLogger(string categoryName) => new FileLogger(_filePath, categoryName);

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _filePath;
        private readonly string _category;
        private static readonly object Lock = new();

        public FileLogger(string filePath, string category)
        {
            _filePath = filePath;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel}] [{_category}] {message}";
            if (exception != null)
                line += $"{Environment.NewLine}  {exception}";
            line += Environment.NewLine;

            try
            {
                lock (Lock)
                {
                    File.AppendAllText(_filePath, line);
                }
            }
            catch { /* swallow file I/O errors in logging */ }
        }
    }
}
