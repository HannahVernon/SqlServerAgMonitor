using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SqlAgMonitor.Core.Services.Connection;

public class ReconnectingConnectionWrapper : IAsyncDisposable
{
    private readonly ISqlConnectionService _connectionService;
    private readonly ILogger _logger;
    private readonly string _server;
    private readonly string? _username;
    private readonly string? _credentialKey;
    private readonly string _authType;

    private SqlConnection? _connection;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Subject<ConnectionStateChange> _stateChanges = new();
    private bool _disposed;

    private int _reconnectAttempt;
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 32, 60];

    public IObservable<ConnectionStateChange> StateChanges => _stateChanges.AsObservable();
    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;
    public string Server => _server;

    public ReconnectingConnectionWrapper(
        ISqlConnectionService connectionService,
        ILogger logger,
        string server,
        string? username,
        string? credentialKey,
        string authType)
    {
        _connectionService = connectionService;
        _logger = logger;
        _server = server;
        _username = username;
        _credentialKey = credentialKey;
        _authType = authType;
    }

    public async Task<SqlConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_connection?.State == System.Data.ConnectionState.Open)
                return _connection;

            _connection?.Dispose();
            _connection = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _connection = await _connectionService.GetConnectionAsync(
                        _server, _username, _credentialKey, _authType, cancellationToken);
                    _reconnectAttempt = 0;
                    _stateChanges.OnNext(new ConnectionStateChange(_server, true, null, DateTimeOffset.UtcNow));
                    return _connection;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var backoff = BackoffSeconds[Math.Min(_reconnectAttempt, BackoffSeconds.Length - 1)];
                    _reconnectAttempt++;
                    _logger.LogWarning(ex, "Connection to {Server} failed (attempt {Attempt}). Retrying in {Backoff}s.",
                        _server, _reconnectAttempt, backoff);
                    _stateChanges.OnNext(new ConnectionStateChange(_server, false, ex.Message, DateTimeOffset.UtcNow));
                    await Task.Delay(TimeSpan.FromSeconds(backoff), cancellationToken);
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void InvalidateConnection()
    {
        _connection?.Dispose();
        _connection = null;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _connection?.Dispose();
            _stateChanges.OnCompleted();
            _stateChanges.Dispose();
            _semaphore.Dispose();
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }
}
