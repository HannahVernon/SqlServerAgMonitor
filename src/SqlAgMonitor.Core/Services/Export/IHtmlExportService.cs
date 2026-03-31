using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Export;

public interface IHtmlExportService : IDisposable
{
    Task ExportAsync(IReadOnlyList<MonitoredGroupSnapshot> snapshots, string outputPath, CancellationToken cancellationToken = default);
    Task StartScheduledExportAsync(Func<IReadOnlyList<MonitoredGroupSnapshot>> snapshotProvider, CancellationToken cancellationToken = default);
    Task StopScheduledExportAsync();
}
