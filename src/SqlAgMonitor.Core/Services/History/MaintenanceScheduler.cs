using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Runs periodic background maintenance tasks: event pruning and snapshot summarization.
/// Owns its own timer subscriptions and disposes them when the scheduler is disposed.
/// </summary>
public sealed class MaintenanceScheduler : IDisposable
{
    private readonly CompositeDisposable _timers = new();

    public MaintenanceScheduler(
        IHistoryMaintenanceService maintenance,
        IConfigurationService configService,
        ILogger<MaintenanceScheduler> logger)
    {
        // Prune old events: 10s initial delay (let app finish startup), then every 24 hours
        var pruneSub = Observable.Timer(TimeSpan.FromSeconds(10), TimeSpan.FromHours(24))
            .SelectMany(_ => Observable.FromAsync(async ct =>
            {
                try
                {
                    var config = configService.Load();
                    if (!config.History.AutoPruneEnabled) return;
                    await maintenance.PruneEventsAsync(config.History.MaxRetentionDays, config.History.MaxRecords, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to prune old events.");
                }
            }))
            .Subscribe();
        _timers.Add(pruneSub);

        // Summarize snapshots: 5 min initial delay (accumulate raw data first), then hourly.
        // The 3-step aggregation: raw → hourly (after RawRetentionHours), hourly → daily
        // (after HourlyRetentionDays), then prune aged daily (after DailyRetentionDays).
        var summarizeSub = Observable.Timer(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1))
            .SelectMany(_ => Observable.FromAsync(async ct =>
            {
                try
                {
                    var config = configService.Load();
                    var retention = config.History.SnapshotRetention;
                    await maintenance.SummarizeSnapshotsAsync(
                        retention.RawRetentionHours,
                        retention.HourlyRetentionDays,
                        retention.DailyRetentionDays,
                        ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to summarize snapshots.");
                }
            }))
            .Subscribe();
        _timers.Add(summarizeSub);
    }

    public void Dispose() => _timers.Dispose();
}
