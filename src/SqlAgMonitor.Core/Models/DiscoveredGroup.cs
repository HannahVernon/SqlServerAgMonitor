using ReactiveUI;

namespace SqlAgMonitor.Core.Models;

public class DiscoveredGroup : ReactiveObject
{
    private bool _isSelected = true;

    public required string Name { get; init; }
    public required AvailabilityGroupType GroupType { get; init; }
    public required ReplicaRole LocalRole { get; init; }
    public required string ServerName { get; init; }
    public List<string> ReplicaServers { get; init; } = new();

    /// <summary>For DAGs: discovered member AG info (listeners, roles, local instances).</summary>
    public List<DagMemberDiscovery> DagMembers { get; init; } = new();

    /// <summary>Whether this group is selected for monitoring in the wizard.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

/// <summary>A member AG within a distributed AG, discovered during the wizard.</summary>
public class DagMemberDiscovery
{
    /// <summary>AG listener name for this DAG member.</summary>
    public string MemberAgName { get; init; } = string.Empty;

    /// <summary>Whether this member's AG resides on the server we connected to.</summary>
    public bool IsLocal { get; init; }

    /// <summary>PRIMARY or SECONDARY at the DAG level.</summary>
    public string? DagRoleDesc { get; init; }

    /// <summary>For the local member: actual SERVER\INSTANCE names within the member AG.</summary>
    public List<DagInstanceDiscovery> Instances { get; init; } = new();
}

/// <summary>A specific SQL Server instance within a DAG member AG.</summary>
public class DagInstanceDiscovery
{
    /// <summary>The actual SERVER\INSTANCE name.</summary>
    public string ServerName { get; init; } = string.Empty;

    /// <summary>PRIMARY or SECONDARY within the member AG.</summary>
    public string? RoleDesc { get; init; }

    /// <summary>Whether this is the instance our discovery connection landed on.</summary>
    public bool IsLocal { get; init; }
}
