using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Connection;

namespace SqlAgMonitor.Core.Services.Monitoring;

public class AgControlService : IAgControlService
{
    private readonly ISqlConnectionService _connectionService;
    private readonly ILogger<AgControlService> _logger;

    public AgControlService(
        ISqlConnectionService connectionService,
        ILogger<AgControlService> logger)
    {
        _connectionService = connectionService;
        _logger = logger;
    }

    public async Task<bool> FailoverAsync(
        string agName,
        string targetReplica,
        CancellationToken cancellationToken = default)
    {
        var sql = $"ALTER AVAILABILITY GROUP [{EscapeBrackets(agName)}] FAILOVER;";
        return await ExecuteOnReplicaAsync(targetReplica, sql, "Failover", agName, cancellationToken);
    }

    public async Task<bool> ForceFailoverAsync(
        string agName,
        string targetReplica,
        CancellationToken cancellationToken = default)
    {
        var sql = $"ALTER AVAILABILITY GROUP [{EscapeBrackets(agName)}] FORCE_FAILOVER_ALLOW_DATA_LOSS;";
        return await ExecuteOnReplicaAsync(targetReplica, sql, "ForceFailover", agName, cancellationToken);
    }

    public async Task<bool> SetAvailabilityModeAsync(
        string agName,
        string replicaName,
        AvailabilityMode mode,
        CancellationToken cancellationToken = default)
    {
        var modeString = mode switch
        {
            AvailabilityMode.SynchronousCommit => "SYNCHRONOUS_COMMIT",
            AvailabilityMode.AsynchronousCommit => "ASYNCHRONOUS_COMMIT",
            AvailabilityMode.ConfigurationOnly => "CONFIGURATION_ONLY",
            _ => throw new ArgumentException($"Cannot set availability mode to '{mode}'.", nameof(mode))
        };

        var sql = $"ALTER AVAILABILITY GROUP [{EscapeBrackets(agName)}] " +
                  $"MODIFY REPLICA ON N'{EscapeQuotes(replicaName)}' " +
                  $"WITH (AVAILABILITY_MODE = {modeString});";

        return await ExecuteOnReplicaAsync(replicaName, sql, "SetAvailabilityMode", agName, cancellationToken);
    }

    public async Task<bool> SuspendDatabaseAsync(
        string agName,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var sql = $"ALTER DATABASE [{EscapeBrackets(databaseName)}] SET HADR SUSPEND;";
        return await ExecuteLocalAsync(sql, "SuspendDatabase", agName, databaseName, cancellationToken);
    }

    public async Task<bool> ResumeDatabaseAsync(
        string agName,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var sql = $"ALTER DATABASE [{EscapeBrackets(databaseName)}] SET HADR RESUME;";
        return await ExecuteLocalAsync(sql, "ResumeDatabase", agName, databaseName, cancellationToken);
    }

    private async Task<bool> ExecuteOnReplicaAsync(
        string server,
        string sql,
        string operationName,
        string agName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Executing {Operation} on AG '{AgName}' targeting server '{Server}'.",
                operationName, agName, server);

            var connection = await _connectionService.GetConnectionAsync(
                server, username: null, credentialKey: null, authType: "windows", cancellationToken);

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation(
                    "{Operation} completed successfully on AG '{AgName}' (server '{Server}').",
                    operationName, agName, server);
                return true;
            }
            finally
            {
                _connectionService.ReturnConnection(server, connection);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Operation} failed on AG '{AgName}' targeting server '{Server}'.",
                operationName, agName, server);
            return false;
        }
    }

    private async Task<bool> ExecuteLocalAsync(
        string sql,
        string operationName,
        string agName,
        string databaseName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Executing {Operation} on database '{Database}' in AG '{AgName}'.",
                operationName, databaseName, agName);

            var connection = await _connectionService.GetConnectionAsync(
                ".", username: null, credentialKey: null, authType: "windows", cancellationToken);

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation(
                    "{Operation} completed successfully on database '{Database}' in AG '{AgName}'.",
                    operationName, databaseName, agName);
                return true;
            }
            finally
            {
                _connectionService.ReturnConnection(".", connection);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Operation} failed on database '{Database}' in AG '{AgName}'.",
                operationName, databaseName, agName);
            return false;
        }
    }

    private static string EscapeBrackets(string value) =>
        value.Replace("]", "]]");

    private static string EscapeQuotes(string value) =>
        value.Replace("'", "''");
}
