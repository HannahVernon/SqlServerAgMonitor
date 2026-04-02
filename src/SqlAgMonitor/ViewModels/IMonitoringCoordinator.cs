using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.ViewModels;

/// <summary>
/// Abstraction over the monitoring coordinator, allowing MainWindowViewModel to work
/// with either direct SQL monitoring (<see cref="MonitoringCoordinator"/>) or remote
/// service-client mode (<see cref="SqlAgMonitor.Services.ServiceMonitoringClient"/>).
/// </summary>
public interface IMonitoringCoordinator : IDisposable
{
    ObservableCollection<MonitorTabViewModel> MonitorTabs { get; }

    event Action<MonitoredGroupSnapshot>? SnapshotProcessed;
    event Action<AlertEvent>? AlertRaised;

    void SubscribeToMonitors();
    Task LoadAndStartAsync();
    Task StartGroupAsync(string groupName, AvailabilityGroupType groupType);
    Task StopGroupAsync(string groupName, AvailabilityGroupType groupType);
    Task<MonitoredGroupSnapshot> PollOnceAsync(string groupName, AvailabilityGroupType groupType);
    MonitorTabViewModel? FindTab(string name);
    IReadOnlyList<MonitoredGroupSnapshot> GetLatestSnapshots();
    Task DisposeMonitorsAsync();
}
