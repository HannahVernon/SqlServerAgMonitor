using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using ReactiveUI;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.ViewModels;

public class MonitorTabViewModel : ViewModelBase
{
    private string _tabTitle = string.Empty;
    private AvailabilityGroupType _groupType;
    private SynchronizationHealth _overallHealth;
    private bool _isPaused;
    private bool _isConnected;
    private string _filterDescription = "All replicas";
    private IBrush _healthBrush = Brushes.Gray;
    private AvailabilityGroupInfo? _agInfo;
    private DistributedAgInfo? _dagInfo;
    private string? _selectedReplicaName;
    private List<ReplicaColumnInfo> _replicaColumns = new();

    private List<DatabaseReplicaState> _allDatabaseStates = new();

    public string TabTitle
    {
        get => _tabTitle;
        set => this.RaiseAndSetIfChanged(ref _tabTitle, value);
    }

    public AvailabilityGroupType GroupType
    {
        get => _groupType;
        set => this.RaiseAndSetIfChanged(ref _groupType, value);
    }

    public SynchronizationHealth OverallHealth
    {
        get => _overallHealth;
        set
        {
            this.RaiseAndSetIfChanged(ref _overallHealth, value);
            HealthBrush = value switch
            {
                SynchronizationHealth.Healthy => Brushes.LimeGreen,
                SynchronizationHealth.PartiallyHealthy => Brushes.Orange,
                SynchronizationHealth.NotHealthy => Brushes.Red,
                _ => Brushes.Gray
            };
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => this.RaiseAndSetIfChanged(ref _isPaused, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public string FilterDescription
    {
        get => _filterDescription;
        set => this.RaiseAndSetIfChanged(ref _filterDescription, value);
    }

    public IBrush HealthBrush
    {
        get => _healthBrush;
        set => this.RaiseAndSetIfChanged(ref _healthBrush, value);
    }

    public AvailabilityGroupInfo? AgInfo
    {
        get => _agInfo;
        set => this.RaiseAndSetIfChanged(ref _agInfo, value);
    }

    public DistributedAgInfo? DagInfo
    {
        get => _dagInfo;
        set => this.RaiseAndSetIfChanged(ref _dagInfo, value);
    }

    public string? SelectedReplicaName
    {
        get => _selectedReplicaName;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedReplicaName, value);
            BuildPivotRows();
        }
    }

    /// <summary>Flat list of all database states (kept for export/alerting).</summary>
    public ObservableCollection<DatabaseReplicaState> DatabaseStates { get; } = new();

    /// <summary>Pivoted rows: one row per database, with per-replica LSN columns.</summary>
    public ObservableCollection<DatabasePivotRow> PivotRows { get; } = new();

    /// <summary>Metadata for dynamic replica columns. Changes when replica set changes.</summary>
    public List<ReplicaColumnInfo> ReplicaColumns
    {
        get => _replicaColumns;
        private set => this.RaiseAndSetIfChanged(ref _replicaColumns, value);
    }

    /// <summary>Fired when the set of replica columns changes and the DataGrid needs rebuilding.</summary>
    public event Action? ReplicaColumnsChanged;

    public ObservableCollection<ReplicaInfo> Replicas { get; } = new();

    public void ApplySnapshot(MonitoredGroupSnapshot snapshot)
    {
        IsConnected = snapshot.IsConnected;
        OverallHealth = snapshot.OverallHealth;
        AgInfo = snapshot.AgInfo;
        DagInfo = snapshot.DagInfo;

        _allDatabaseStates.Clear();
        Replicas.Clear();

        if (snapshot.AgInfo is { } agInfo)
        {
            foreach (var replica in agInfo.Replicas)
            {
                Replicas.Add(replica);
                foreach (var dbState in replica.DatabaseStates)
                    _allDatabaseStates.Add(dbState);
            }
        }

        if (snapshot.DagInfo is { } dagInfo)
        {
            foreach (var member in dagInfo.Members)
            {
                if (member.LocalAgInfo is null) continue;
                foreach (var replica in member.LocalAgInfo.Replicas)
                {
                    Replicas.Add(replica);
                    foreach (var dbState in replica.DatabaseStates)
                        _allDatabaseStates.Add(dbState);
                }
            }
        }

        BuildReplicaColumns();
        BuildPivotRows();
    }

    private void BuildReplicaColumns()
    {
        var newColumns = Replicas
            .Select((r, i) => new ReplicaColumnInfo
            {
                ReplicaName = r.ReplicaServerName,
                IsPrimary = r.Role == ReplicaRole.Primary,
                Index = i
            })
            .OrderByDescending(c => c.IsPrimary)
            .ThenBy(c => c.ReplicaName, StringComparer.OrdinalIgnoreCase)
            .Select((c, i) => new ReplicaColumnInfo
            {
                ReplicaName = c.ReplicaName,
                IsPrimary = c.IsPrimary,
                Index = i
            })
            .ToList();

        // Only signal column rebuild if the set actually changed
        var oldHeaders = _replicaColumns.Select(c => c.Header).ToList();
        var newHeaders = newColumns.Select(c => c.Header).ToList();

        ReplicaColumns = newColumns;

        if (!oldHeaders.SequenceEqual(newHeaders))
        {
            ReplicaColumnsChanged?.Invoke();
        }
    }

    private void BuildPivotRows()
    {
        PivotRows.Clear();
        DatabaseStates.Clear();

        var filtered = _allDatabaseStates.AsEnumerable();

        if (!string.IsNullOrEmpty(SelectedReplicaName))
        {
            filtered = filtered.Where(d =>
                string.Equals(d.ReplicaServerName, SelectedReplicaName, StringComparison.OrdinalIgnoreCase));
            FilterDescription = $"Filtered: {SelectedReplicaName}";
        }
        else
        {
            FilterDescription = "All replicas";
        }

        var statesList = filtered.ToList();
        foreach (var state in statesList)
            DatabaseStates.Add(state);

        // Build pivot: group by database name
        var dbGroups = statesList
            .GroupBy(d => d.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var replicaColumns = ReplicaColumns;
        var replicaCount = replicaColumns.Count;

        foreach (var dbGroup in dbGroups)
        {
            var lsns = new decimal[replicaCount];
            var syncStates = new string[replicaCount];

            for (int i = 0; i < replicaCount; i++)
            {
                var colInfo = replicaColumns[i];
                var match = dbGroup.FirstOrDefault(d =>
                    string.Equals(d.ReplicaServerName, colInfo.ReplicaName, StringComparison.OrdinalIgnoreCase));

                lsns[i] = match?.LastHardenedLsn ?? 0;
                syncStates[i] = match?.SynchronizationState.ToString() ?? "";
            }

            var allStates = dbGroup.ToList();
            var maxDiff = allStates.Count > 0
                ? allStates.Max(d => d.LsnDifferenceFromPrimary)
                : 0;
            var worstSync = allStates
                .OrderByDescending(d => d.SynchronizationState)
                .FirstOrDefault()?.SynchronizationState.ToString() ?? "Unknown";
            var anySuspended = allStates.Any(d => d.IsSuspended);

            var row = new DatabasePivotRow
            {
                DatabaseName = dbGroup.Key,
                MaxLsnDiff = maxDiff,
                WorstSyncState = worstSync,
                AnySuspended = anySuspended
            };
            row.SetReplicaValues(lsns, syncStates);
            PivotRows.Add(row);
        }
    }
}
