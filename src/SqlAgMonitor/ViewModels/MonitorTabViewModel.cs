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
            ApplyReplicaFilter();
        }
    }

    public ObservableCollection<DatabaseReplicaState> DatabaseStates { get; } = new();

    public ObservableCollection<ReplicaInfo> Replicas { get; } = new();

    public void ApplySnapshot(MonitoredGroupSnapshot snapshot)
    {
        IsConnected = snapshot.IsConnected;
        OverallHealth = snapshot.OverallHealth;
        AgInfo = snapshot.AgInfo;
        DagInfo = snapshot.DagInfo;

        // Collect all database states from all replicas
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

        ApplyReplicaFilter();
    }

    private void ApplyReplicaFilter()
    {
        DatabaseStates.Clear();

        IEnumerable<DatabaseReplicaState> filtered = _allDatabaseStates;

        if (!string.IsNullOrEmpty(SelectedReplicaName))
        {
            filtered = _allDatabaseStates
                .Where(d => string.Equals(d.ReplicaServerName, SelectedReplicaName, StringComparison.OrdinalIgnoreCase));
            FilterDescription = $"Filtered: {SelectedReplicaName}";
        }
        else
        {
            FilterDescription = "All replicas";
        }

        foreach (var state in filtered)
            DatabaseStates.Add(state);
    }
}
