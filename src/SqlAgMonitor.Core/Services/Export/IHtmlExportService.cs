using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Export;

public interface IHtmlExportService
{
    Task ExportAsync(IReadOnlyList<MonitoredGroupSnapshot> snapshots, string outputPath, CancellationToken cancellationToken = default);
    Task StartScheduledExportAsync(CancellationToken cancellationToken = default);
    Task StopScheduledExportAsync();
}
