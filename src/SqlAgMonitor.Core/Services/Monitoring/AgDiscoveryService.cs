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
            COALESCE(ag.[is_distributed], 0) AS [is_distributed],
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

    /// <summary>
    /// Gets DAG-level member AG names with their roles and is_local flag.
    /// </summary>
    private const string DagMembersSql = @"
        SELECT
            ag.[name]                   AS [dag_name],
            ar.[replica_server_name]    AS [member_ag_name],
            ars.[role_desc],
            ars.[is_local]
        FROM sys.availability_groups ag
            INNER JOIN sys.availability_replicas ar
                ON ag.[group_id] = ar.[group_id]
            INNER JOIN sys.dm_hadr_availability_replica_states ars
                ON ar.[replica_id] = ars.[replica_id]
        WHERE ag.[is_distributed] = 1
        ORDER BY ag.[name], ar.[replica_server_name];
    ";

    /// <summary>
    /// Gets actual SERVER\INSTANCE names for the LOCAL member AG's replicas.
    /// Uses fn_hadr_distributed_ag_replica to drill from DAG → local AG → its replicas.
    /// Only resolves for the local member (remote member's group_id won't exist locally).
    /// The is_local column identifies which instance our connection landed on.
    /// </summary>
    private const string DagLocalInstancesSql = @"
        SELECT
            [ag].[name]                                     AS [distributed_ag_name],
            [dag_ar].[replica_server_name],
            [dag_arsl].[role_desc]                          AS [replica_server_role],
            [dag_arsl].[is_local]
        FROM [sys].[availability_groups]                             AS [ag]
            INNER JOIN [sys].[availability_replicas]                 AS [ar]
                ON [ag].[group_id] = [ar].[group_id]
            INNER JOIN [sys].[dm_hadr_availability_replica_states]   AS [ars]
                ON [ar].[replica_id] = [ars].[replica_id]
            CROSS APPLY [sys].[fn_hadr_distributed_ag_replica]([ag].[group_id], [ar].[replica_id]) AS [hdar]
            INNER JOIN [sys].[availability_groups]                   AS [ag1]
                ON [hdar].[group_id] = [ag1].[group_id]
            INNER JOIN [sys].[availability_replicas]                 AS [dag_ar]
                ON [ag1].[group_id] = [dag_ar].[group_id]
            INNER JOIN [sys].[dm_hadr_availability_replica_states]   AS [dag_arsl]
                ON [dag_ar].[replica_id] = [dag_arsl].[replica_id]
        WHERE [ag].[is_distributed] = 1
        ORDER BY [ag].[name], [dag_ar].[replica_server_name];
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
            server, username, credentialKey, authType, cancellationToken: cancellationToken);

        var groups = await DiscoverGroupsCoreAsync(connection, server, cancellationToken);

        // For any discovered DAGs, run additional queries to get member details
        if (groups.Values.Any(g => g.GroupType == AvailabilityGroupType.DistributedAvailabilityGroup))
        {
            await DiscoverDagMembersAsync(connection, groups, cancellationToken);
        }

        var result = groups.Values.ToList();
        _logger.LogInformation("Discovered {Count} AG/DAG(s) on {Server}.", result.Count, server);
        return result;
    }

    private async Task<Dictionary<string, DiscoveredGroup>> DiscoverGroupsCoreAsync(
        SqlConnection connection, string server, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = DiscoverySql;

        var groups = new Dictionary<string, DiscoveredGroup>();

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var agName = reader.GetString(0);
            var isDistributed = Convert.ToBoolean(reader.GetValue(1));
            var roleDesc = reader.IsDBNull(2) ? null : reader.GetString(2);
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

        return groups;
    }

    private async Task DiscoverDagMembersAsync(
        SqlConnection connection, Dictionary<string, DiscoveredGroup> groups, CancellationToken ct)
    {
        // Query 1: DAG-level members (AG listener names + roles + is_local)
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = DagMembersSql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var dagName = reader.GetString(0);
                var memberAgName = reader.GetString(1);
                var roleDesc = reader.IsDBNull(2) ? null : reader.GetString(2);
                var isLocal = !reader.IsDBNull(3) && reader.GetBoolean(3);

                if (groups.TryGetValue(dagName, out var group))
                {
                    group.DagMembers.Add(new DagMemberDiscovery
                    {
                        MemberAgName = memberAgName,
                        IsLocal = isLocal,
                        DagRoleDesc = roleDesc
                    });
                }
            }
        }

        // Query 2: Local member's actual SERVER\INSTANCE names
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = DagLocalInstancesSql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var dagName = reader.GetString(0);
                var instanceName = reader.GetString(1);
                var roleDesc = reader.IsDBNull(2) ? null : reader.GetString(2);
                var isLocal = !reader.IsDBNull(3) && reader.GetBoolean(3);

                if (groups.TryGetValue(dagName, out var group))
                {
                    var localMember = group.DagMembers.FirstOrDefault(m => m.IsLocal);
                    localMember?.Instances.Add(new DagInstanceDiscovery
                    {
                        ServerName = instanceName,
                        RoleDesc = roleDesc,
                        IsLocal = isLocal
                    });
                }
            }
        }
    }

    private static ReplicaRole ParseRole(string? roleDesc) => roleDesc?.ToUpperInvariant() switch
    {
        "PRIMARY" => ReplicaRole.Primary,
        "SECONDARY" => ReplicaRole.Secondary,
        "RESOLVING" => ReplicaRole.Resolving,
        _ => ReplicaRole.Unknown
    };
}
