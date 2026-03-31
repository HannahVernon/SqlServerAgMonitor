namespace SqlAgMonitor.Core.Services.History;

public class FileErrorLogger : IDisposable
{
    private readonly string _logDirectory;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxFiles;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _currentLogPath;
    private bool _disposed;

    /// <summary>
    /// Simple file-based error logger with daily rotation and size limits.
    /// Used as a last-resort logger when DI/ILogger infrastructure is unavailable.
    /// Errors within logging are silently swallowed to avoid recursive failures.
    /// </summary>
    /// <param name="logDirectory">Log directory; defaults to %APPDATA%/SqlAgMonitor/logs.</param>
    /// <param name="maxFileSizeBytes">Max size per log file before rotation. Default: 10 MB.</param>
    /// <param name="maxFiles">Max rotated log files to keep (oldest deleted). Default: 5.</param>
    public FileErrorLogger(string? logDirectory = null, long maxFileSizeBytes = 10 * 1024 * 1024, int maxFiles = 5)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlAgMonitor", "logs");
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxFiles = maxFiles;
        Directory.CreateDirectory(_logDirectory);
    }

    public void LogError(string errorType, string message, string? stackTrace = null, string? context = null)
    {
        lock (_lock)
        {
            try
            {
                EnsureWriter();
                var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
                _writer!.WriteLine($"[{timestamp}] [{errorType}] {message}");
                if (context != null)
                    _writer.WriteLine($"  Context: {context}");
                if (stackTrace != null)
                    _writer.WriteLine($"  StackTrace: {stackTrace}");
                _writer.WriteLine();
                _writer.Flush();
            }
            catch (Exception)
            {
                // Swallow — we can't log an error about failing to log
            }
        }
    }

    public void LogInfo(string message)
    {
        lock (_lock)
        {
            try
            {
                EnsureWriter();
                var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
                _writer!.WriteLine($"[{timestamp}] [INFO] {message}");
                _writer.Flush();
            }
            catch (Exception)
            {
                // Swallow
            }
        }
    }

    private void EnsureWriter()
    {
        var logFileName = $"sqlagmonitor-{DateTime.UtcNow:yyyyMMdd}.log";
        var logPath = Path.Combine(_logDirectory, logFileName);

        if (_currentLogPath != logPath || _writer == null)
        {
            _writer?.Dispose();
            _currentLogPath = logPath;
            _writer = new StreamWriter(logPath, append: true) { AutoFlush = false };
        }

        var fileInfo = new FileInfo(logPath);
        if (fileInfo.Exists && fileInfo.Length >= _maxFileSizeBytes)
        {
            _writer.Dispose();
            RotateFiles(logPath);
            _writer = new StreamWriter(logPath, append: false) { AutoFlush = false };
        }
    }

    private void RotateFiles(string basePath)
    {
        for (int i = _maxFiles - 1; i >= 1; i--)
        {
            var oldPath = $"{basePath}.{i}";
            var newPath = $"{basePath}.{i + 1}";
            if (File.Exists(newPath)) File.Delete(newPath);
            if (File.Exists(oldPath)) File.Move(oldPath, newPath);
        }

        if (File.Exists(basePath))
        {
            var rotatedPath = $"{basePath}.1";
            File.Move(basePath, rotatedPath);
        }

        var excess = $"{basePath}.{_maxFiles + 1}";
        if (File.Exists(excess)) File.Delete(excess);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
            _disposed = true;
        }
    }
}
