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
        var sql = "DECLARE @sql nvarchar(500) = N'ALTER AVAILABILITY GROUP ' + QUOTENAME(@agName) + N' FAILOVER;'; EXEC(@sql);";
        var parameters = new[] { new SqlParameter("@agName", agName) };
        return await ExecuteOnReplicaAsync(targetReplica, sql, parameters, "Failover", agName, cancellationToken);
    }

    public async Task<bool> ForceFailoverAsync(
        string agName,
        string targetReplica,
        CancellationToken cancellationToken = default)
    {
        var sql = "DECLARE @sql nvarchar(500) = N'ALTER AVAILABILITY GROUP ' + QUOTENAME(@agName) + N' FORCE_FAILOVER_ALLOW_DATA_LOSS;'; EXEC(@sql);";
        var parameters = new[] { new SqlParameter("@agName", agName) };
        return await ExecuteOnReplicaAsync(targetReplica, sql, parameters, "ForceFailover", agName, cancellationToken);
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

        var sql = "DECLARE @sql nvarchar(500) = N'ALTER AVAILABILITY GROUP ' + QUOTENAME(@agName)"
                + " + N' MODIFY REPLICA ON ' + QUOTENAME(@replicaName, '''')"
                + $" + N' WITH (AVAILABILITY_MODE = {modeString});';"
                + " EXEC(@sql);";
        var parameters = new[]
        {
            new SqlParameter("@agName", agName),
            new SqlParameter("@replicaName", replicaName)
        };

        return await ExecuteOnReplicaAsync(replicaName, sql, parameters, "SetAvailabilityMode", agName, cancellationToken);
    }

    public async Task<bool> SuspendDatabaseAsync(
        string agName,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var sql = "DECLARE @sql nvarchar(500) = N'ALTER DATABASE ' + QUOTENAME(@dbName) + N' SET HADR SUSPEND;'; EXEC(@sql);";
        var parameters = new[] { new SqlParameter("@dbName", databaseName) };
        return await ExecuteLocalAsync(sql, parameters, "SuspendDatabase", agName, databaseName, cancellationToken);
    }

    public async Task<bool> ResumeDatabaseAsync(
        string agName,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var sql = "DECLARE @sql nvarchar(500) = N'ALTER DATABASE ' + QUOTENAME(@dbName) + N' SET HADR RESUME;'; EXEC(@sql);";
        var parameters = new[] { new SqlParameter("@dbName", databaseName) };
        return await ExecuteLocalAsync(sql, parameters, "ResumeDatabase", agName, databaseName, cancellationToken);
    }

    private async Task<bool> ExecuteOnReplicaAsync(
        string server,
        string sql,
        SqlParameter[] parameters,
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
                server, username: null, credentialKey: null, authType: "windows", cancellationToken: cancellationToken);

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 120;
                cmd.Parameters.AddRange(parameters);
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
        SqlParameter[] parameters,
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
                ".", username: null, credentialKey: null, authType: "windows", cancellationToken: cancellationToken);

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 120;
                cmd.Parameters.AddRange(parameters);
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
}
