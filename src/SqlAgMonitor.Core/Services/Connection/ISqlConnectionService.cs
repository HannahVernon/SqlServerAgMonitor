using Microsoft.Data.SqlClient;

namespace SqlAgMonitor.Core.Services.Connection;

public interface ISqlConnectionService : IAsyncDisposable
{
    Task<SqlConnection> GetConnectionAsync(string server, string? username, string? credentialKey, string authType,
        bool encrypt = true, bool trustServerCertificate = false,
        CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(string server, string? username, string? credentialKey, string authType,
        bool encrypt = true, bool trustServerCertificate = false,
        CancellationToken cancellationToken = default);
    void ReturnConnection(string server, SqlConnection connection);
}
