using System.Text;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Export;

public class HtmlExportService : IHtmlExportService
{
    private readonly IConfigurationService _configService;
    private readonly ILogger<HtmlExportService> _logger;
    private Timer? _exportTimer;

    public HtmlExportService(IConfigurationService configService, ILogger<HtmlExportService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public async Task ExportAsync(IReadOnlyList<MonitoredGroupSnapshot> snapshots, string outputPath, CancellationToken cancellationToken = default)
    {
        var html = GenerateHtml(snapshots);
        var dir = Path.GetDirectoryName(outputPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var fileName = $"ag-monitor-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.html";
        var fullPath = Path.Combine(outputPath, fileName);
        await File.WriteAllTextAsync(fullPath, html, cancellationToken);
        _logger.LogInformation("HTML report exported to {Path}.", fullPath);
    }

    public Task StartScheduledExportAsync(CancellationToken cancellationToken = default)
    {
        var config = _configService.Load();
        if (!config.Export.Enabled || string.IsNullOrEmpty(config.Export.ExportPath))
        {
            _logger.LogInformation("Scheduled HTML export is disabled.");
            return Task.CompletedTask;
        }

        var interval = TimeSpan.FromHours(config.Export.ScheduleIntervalHours);
        _exportTimer = new Timer(_ =>
        {
            _logger.LogInformation("Scheduled export timer fired.");
        }, null, interval, interval);

        _logger.LogInformation("Scheduled HTML export started (every {Hours}h to {Path}).",
            config.Export.ScheduleIntervalHours, config.Export.ExportPath);
        return Task.CompletedTask;
    }

    public Task StopScheduledExportAsync()
    {
        _exportTimer?.Dispose();
        _exportTimer = null;
        _logger.LogInformation("Scheduled HTML export stopped.");
        return Task.CompletedTask;
    }

    private static string GenerateHtml(IReadOnlyList<MonitoredGroupSnapshot> snapshots)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head>");
        sb.AppendLine("<meta charset='utf-8'>");
        sb.AppendLine("<title>SQL Server AG Monitor Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', sans-serif; background: #1e1e2e; color: #cdd6f4; margin: 20px; }");
        sb.AppendLine("h1 { color: #89b4fa; }");
        sb.AppendLine("h2 { color: #a6e3a1; border-bottom: 1px solid #45475a; padding-bottom: 4px; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; margin-bottom: 20px; }");
        sb.AppendLine("th, td { border: 1px solid #45475a; padding: 8px; text-align: left; }");
        sb.AppendLine("th { background: #313244; }");
        sb.AppendLine(".healthy { color: #a6e3a1; } .partial { color: #fab387; } .unhealthy { color: #f38ba8; } .unknown { color: #6c7086; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>SQL Server AG Monitor Report</h1>");
        sb.AppendLine($"<p>Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>");

        foreach (var snapshot in snapshots)
        {
            var healthClass = snapshot.OverallHealth switch
            {
                SynchronizationHealth.Healthy => "healthy",
                SynchronizationHealth.PartiallyHealthy => "partial",
                SynchronizationHealth.NotHealthy => "unhealthy",
                _ => "unknown"
            };

            sb.AppendLine($"<h2>{snapshot.Name} <span class='{healthClass}'>[{snapshot.OverallHealth}]</span></h2>");
            sb.AppendLine($"<p>Type: {snapshot.GroupType} | Connected: {snapshot.IsConnected} | Last Poll: {snapshot.Timestamp:HH:mm:ss}</p>");

            if (snapshot.ErrorMessage != null)
                sb.AppendLine($"<p class='unhealthy'>Error: {snapshot.ErrorMessage}</p>");

            if (snapshot.AgInfo != null)
            {
                sb.AppendLine("<table><tr><th>Replica</th><th>Role</th><th>Connected</th><th>Sync Health</th><th>Mode</th><th>DBs</th></tr>");
                foreach (var r in snapshot.AgInfo.Replicas)
                {
                    sb.AppendLine($"<tr><td>{r.ReplicaServerName}</td><td>{r.Role}</td><td>{r.ConnectedState}</td>");
                    sb.AppendLine($"<td class='{(r.SynchronizationHealth == SynchronizationHealth.Healthy ? "healthy" : "unhealthy")}'>{r.SynchronizationHealth}</td>");
                    sb.AppendLine($"<td>{r.AvailabilityMode}</td><td>{r.DatabaseCount}</td></tr>");
                }
                sb.AppendLine("</table>");

                sb.AppendLine("<table><tr><th>Database</th><th>Replica</th><th>Sync State</th><th>Last Hardened LSN</th><th>LSN Diff</th><th>Log Send Queue</th><th>Redo Queue</th></tr>");
                foreach (var r in snapshot.AgInfo.Replicas)
                {
                    foreach (var d in r.DatabaseStates)
                    {
                        sb.AppendLine($"<tr><td>{d.DatabaseName}</td><td>{d.ReplicaServerName}</td><td>{d.SynchronizationState}</td>");
                        sb.AppendLine($"<td>{d.LastHardenedLsn}</td><td>{d.LsnDifferenceFromPrimary}</td>");
                        sb.AppendLine($"<td>{d.LogSendQueueSizeKb} KB</td><td>{d.RedoQueueSizeKb} KB</td></tr>");
                    }
                }
                sb.AppendLine("</table>");
            }
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
