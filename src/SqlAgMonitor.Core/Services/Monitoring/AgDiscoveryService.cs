using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Connection;

namespace SqlAgMonitor.Core.Services.Monitoring;

public class AgDiscoveryService : IAgDiscoveryService
{
    private readonly ISqlConnectionService _connectionService;
    private readonly ILogger<AgDiscoveryService> _logger;

    private const string DiscoverySql = @"
        SELECT
            ag.[name]                       AS [ag_name],
            ag.[is_distributed],
            ars.[role_desc],
            ar.[replica_server_name]
        FROM sys.availability_groups ag
            INNER JOIN sys.dm_hadr_availability_replica_states ars
                ON ag.[group_id] = ars.[group_id]
                AND ars.[is_local] = 1
            INNER JOIN sys.availability_replicas ar
                ON ag.[group_id] = ar.[group_id]
        ORDER BY ag.[name], ar.[replica_server_name];
    ";

    public AgDiscoveryService(ISqlConnectionService connectionService, ILogger<AgDiscoveryService> logger)
    {
        _connectionService = connectionService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DiscoveredGroup>> DiscoverGroupsAsync(
        string server, string? username, string? credentialKey, string authType,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Discovering AGs/DAGs on {Server}.", server);

        await using var connection = await _connectionService.GetConnectionAsync(
            server, username, credentialKey, authType, cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = DiscoverySql;

        var groups = new Dictionary<string, DiscoveredGroup>();

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var agName = reader.GetString(0);
            var isDistributed = reader.GetBoolean(1);
            var roleDesc = reader.GetString(2);
            var replicaServer = reader.GetString(3);

            if (!groups.TryGetValue(agName, out var group))
            {
                group = new DiscoveredGroup
                {
                    Name = agName,
                    GroupType = isDistributed
                        ? AvailabilityGroupType.DistributedAvailabilityGroup
                        : AvailabilityGroupType.AvailabilityGroup,
                    LocalRole = ParseRole(roleDesc),
                    ServerName = server
                };
                groups[agName] = group;
            }

            group.ReplicaServers.Add(replicaServer);
        }

        var result = groups.Values.ToList();
        _logger.LogInformation("Discovered {Count} AG/DAG(s) on {Server}.", result.Count, server);
        return result;
    }

    private static ReplicaRole ParseRole(string roleDesc) => roleDesc.ToUpperInvariant() switch
    {
        "PRIMARY" => ReplicaRole.Primary,
        "SECONDARY" => ReplicaRole.Secondary,
        "RESOLVING" => ReplicaRole.Resolving,
        _ => ReplicaRole.Unknown
    };
}
