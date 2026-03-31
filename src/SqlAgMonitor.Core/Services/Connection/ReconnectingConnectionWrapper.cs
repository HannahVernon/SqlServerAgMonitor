using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SqlAgMonitor.Core.Services.Connection;

/// <summary>
/// Holds exclusive use of a SQL connection. Dispose to release the lock.
/// </summary>
public sealed class ConnectionLease : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock;
    private readonly ReconnectingConnectionWrapper _owner;
    private bool _released;

    public SqlConnection Connection { get; }

    internal ConnectionLease(SqlConnection connection, SemaphoreSlim usageLock, ReconnectingConnectionWrapper owner)
    {
        Connection = connection;
        _lock = usageLock;
        _owner = owner;
    }

    /// <summary>
    /// Marks the connection as broken and starts background reconnection.
    /// The lease remains held until disposed — no other caller can acquire it.
    /// </summary>
    public void Invalidate()
    {
        _owner.InvalidateConnectionInternal();
    }

    public ValueTask DisposeAsync()
    {
        if (!_released)
        {
            _released = true;
            _lock.Release();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Manages a single persistent SQL connection with background reconnection.
///
/// Design:
/// - <see cref="TryAcquireAsync"/> returns a lease immediately or null if busy (for timer polls).
/// - <see cref="AcquireAsync"/> waits for exclusive access (for manual refresh / initial connect).
/// - The lease holds a SemaphoreSlim for the entire duration of use — no concurrent access.
/// - Reconnection runs in a background task; poll cycles get fast failures while disconnected.
/// </summary>
public class ReconnectingConnectionWrapper : IAsyncDisposable
{
    private readonly ISqlConnectionService _connectionService;
    private readonly ILogger _logger;
    private readonly string _server;
    private readonly string? _username;
    private readonly string? _credentialKey;
    private readonly string _authType;
    private readonly bool _encrypt;
    private readonly bool _trustServerCertificate;

    private SqlConnection? _connection;
    private readonly SemaphoreSlim _usageLock = new(1, 1);
    private readonly Subject<ConnectionStateChange> _stateChanges = new();
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private bool _disposed;

    // Exponential backoff for reconnection attempts: 1s, 2s, 4s, 8s, 16s, 32s, then
    // cap at 60s. Last entry repeats indefinitely for all subsequent attempts.
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
        string authType,
        bool encrypt = true,
        bool trustServerCertificate = false)
    {
        _connectionService = connectionService;
        _logger = logger;
        _server = server;
        _username = username;
        _credentialKey = credentialKey;
        _authType = authType;
        _encrypt = encrypt;
        _trustServerCertificate = trustServerCertificate;
    }

    /// <summary>
    /// Acquires exclusive use of the connection without waiting.
    /// Returns null if another caller holds the lease (previous poll still running).
    /// </summary>
    public async Task<ConnectionLease?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        if (!await _usageLock.WaitAsync(0, cancellationToken))
            return null;

        return await AcquireInternalAsync(cancellationToken);
    }

    /// <summary>
    /// Acquires exclusive use of the connection, waiting if another caller holds the lease.
    /// Use for manual refresh (F5) where the user expects to wait for the result.
    /// </summary>
    public async Task<ConnectionLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _usageLock.WaitAsync(cancellationToken);

        return await AcquireInternalAsync(cancellationToken)
            ?? throw new InvalidOperationException("Failed to acquire connection.");
    }

    /// <summary>
    /// Called with the lock already held. Tries to return an open connection in a lease.
    /// On failure, releases the lock and starts background reconnection.
    /// </summary>
    private async Task<ConnectionLease?> AcquireInternalAsync(CancellationToken cancellationToken)
    {
        // Already connected — return lease
        if (_connection?.State == System.Data.ConnectionState.Open)
            return new ConnectionLease(_connection, _usageLock, this);

        // Not connected — try once
        _connection?.Dispose();
        _connection = null;

        try
        {
            _connection = await _connectionService.GetConnectionAsync(
                _server, _username, _credentialKey, _authType, _encrypt, _trustServerCertificate, cancellationToken);
            _stateChanges.OnNext(new ConnectionStateChange(_server, true, null, DateTimeOffset.UtcNow));
            return new ConnectionLease(_connection, _usageLock, this);
        }
        catch (Exception ex)
        {
            _usageLock.Release();
            _stateChanges.OnNext(new ConnectionStateChange(_server, false, ex.Message, DateTimeOffset.UtcNow));
            StartBackgroundReconnect();
            throw;
        }
    }

    /// <summary>
    /// Called by <see cref="ConnectionLease.Invalidate"/> while the lease is held.
    /// Disposes the connection and starts background reconnection.
    /// </summary>
    internal void InvalidateConnectionInternal()
    {
        _connection?.Dispose();
        _connection = null;
        _stateChanges.OnNext(new ConnectionStateChange(_server, false, "Connection invalidated.", DateTimeOffset.UtcNow));
        StartBackgroundReconnect();
    }

    private void StartBackgroundReconnect()
    {
        if (_disposed || (_reconnectTask != null && !_reconnectTask.IsCompleted))
            return;

        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;

        _reconnectTask = Task.Run(async () => await ReconnectLoopAsync(ct), ct);
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var backoff = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
            _logger.LogInformation("Reconnecting to {Server} in {Backoff}s (attempt {Attempt}).",
                _server, backoff, attempt + 1);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
            }
            catch (OperationCanceledException) { return; }

            // 2s timeout: if a poll cycle holds the lock, skip this attempt and retry
            // after the next backoff delay rather than blocking indefinitely
            if (!await _usageLock.WaitAsync(TimeSpan.FromSeconds(2), ct))
                continue; // Poll is running — try again next iteration

            try
            {
                if (_connection?.State == System.Data.ConnectionState.Open)
                {
                    _logger.LogInformation("Connection to {Server} already restored.", _server);
                    return;
                }

                _connection?.Dispose();
                _connection = null;

                _connection = await _connectionService.GetConnectionAsync(
                    _server, _username, _credentialKey, _authType, _encrypt, _trustServerCertificate, ct);
                _stateChanges.OnNext(new ConnectionStateChange(_server, true, null, DateTimeOffset.UtcNow));
                _logger.LogInformation("Reconnected to {Server}.", _server);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogWarning("Reconnection to {Server} failed (attempt {Attempt}): {Message}",
                    _server, attempt, ex.Message);
                _stateChanges.OnNext(new ConnectionStateChange(_server, false, ex.Message, DateTimeOffset.UtcNow));
            }
            finally
            {
                _usageLock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();

            if (_reconnectTask != null)
            {
                try { await _reconnectTask; }
                catch (OperationCanceledException) { }
            }

            _connection?.Dispose();
            _stateChanges.OnCompleted();
            _stateChanges.Dispose();
            _usageLock.Dispose();
        }
    }
}
