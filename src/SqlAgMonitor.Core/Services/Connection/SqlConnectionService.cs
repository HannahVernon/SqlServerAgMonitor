using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Services.Credentials;

namespace SqlAgMonitor.Core.Services.Connection;

public class SqlConnectionService : ISqlConnectionService, IConnectionMonitor
{
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<SqlConnectionService> _logger;
    private readonly ConcurrentDictionary<string, bool> _connectionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Subject<ConnectionStateChange> _stateChanges = new();
    private bool _disposed;

    public IObservable<ConnectionStateChange> ConnectionStateChanges => _stateChanges.AsObservable();

    public SqlConnectionService(ICredentialStore credentialStore, ILogger<SqlConnectionService> logger)
    {
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public bool IsConnected(string server) =>
        _connectionStates.TryGetValue(server, out var connected) && connected;

    public async Task<SqlConnection> GetConnectionAsync(
        string server, string? username, string? credentialKey, string authType,
        CancellationToken cancellationToken = default)
    {
        var connectionString = await BuildConnectionStringAsync(server, username, credentialKey, authType, cancellationToken);
        var connection = new SqlConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            UpdateConnectionState(server, true, null);
            return connection;
        }
        catch (Exception ex)
        {
            UpdateConnectionState(server, false, ex.Message);
            connection.Dispose();
            throw;
        }
    }

    public async Task<bool> TestConnectionAsync(
        string server, string? username, string? credentialKey, string authType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await GetConnectionAsync(server, username, credentialKey, authType, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test failed for server {Server}.", server);
            return false;
        }
    }

    public void ReturnConnection(string server, SqlConnection connection)
    {
        try
        {
            connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing connection for server {Server}.", server);
        }
    }

    private async Task<string> BuildConnectionStringAsync(
        string server, string? username, string? credentialKey, string authType,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            ApplicationName = "SqlAgMonitor",
            ConnectTimeout = 15,
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            MultiSubnetFailover = true
        };

        if (string.Equals(authType, "windows", StringComparison.OrdinalIgnoreCase))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = username ?? throw new ArgumentException("Username required for SQL authentication.");

            if (string.IsNullOrEmpty(credentialKey))
                throw new ArgumentException("Credential key required for SQL authentication.");

            var password = await _credentialStore.GetPasswordAsync(credentialKey, cancellationToken)
                ?? throw new InvalidOperationException($"No password found in credential store for key '{credentialKey}'.");
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private void UpdateConnectionState(string server, bool isConnected, string? errorMessage)
    {
        var previousState = _connectionStates.TryGetValue(server, out var prev) && prev;
        _connectionStates[server] = isConnected;

        if (previousState != isConnected)
        {
            var change = new ConnectionStateChange(server, isConnected, errorMessage, DateTimeOffset.UtcNow);
            _stateChanges.OnNext(change);

            if (isConnected)
                _logger.LogInformation("Connected to {Server}.", server);
            else
                _logger.LogWarning("Disconnected from {Server}: {Error}", server, errorMessage);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _stateChanges.OnCompleted();
            _stateChanges.Dispose();
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }
}
