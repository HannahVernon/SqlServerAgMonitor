using SqlAgMonitor.Core.Services.Monitoring;
using SqlAgMonitor.Core.Services.History;

namespace SqlAgMonitor.Service;

/// <summary>
/// Headless monitoring coordinator that subscribes to AG/DAG snapshot observables,
/// feeds them through the alert engine, and records events to DuckDB.
/// This is the service-side equivalent of MonitoringCoordinator in the desktop app.
/// </summary>
public sealed class MonitoringWorker : BackgroundService
{
    private readonly ILogger<MonitoringWorker> _logger;

    public MonitoringWorker(ILogger<MonitoringWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MonitoringWorker starting");

        // TODO: Phase 1 — subscribe to AgMonitorService/DagMonitorService snapshots,
        // feed through AlertEngine → AlertDispatcher → IEventRecorder.
        // Track latest snapshots for hub queries.

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MonitoringWorker stopping");
        }
    }
}
