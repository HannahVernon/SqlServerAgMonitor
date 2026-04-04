using System.Globalization;
using ClosedXML.Excel;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Service.Hubs;

/// <summary>
/// Generates Excel files from snapshot data for client-side download via SignalR.
/// </summary>
public static class ExcelExporter
{
    public static byte[] Export(IReadOnlyList<SnapshotDataPoint> data)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Statistics");

        var headers = new[]
        {
            "Timestamp", "Group", "Replica", "Database", "Samples",
            "Send Queue Min (KB)", "Send Queue Max (KB)", "Send Queue Avg (KB)",
            "Redo Queue Min (KB)", "Redo Queue Max (KB)", "Redo Queue Avg (KB)",
            "Send Rate Min (KB/s)", "Send Rate Max (KB/s)", "Send Rate Avg (KB/s)",
            "Redo Rate Min (KB/s)", "Redo Rate Max (KB/s)", "Redo Rate Avg (KB/s)",
            "Log Block Diff Min", "Log Block Diff Max", "Log Block Diff Avg",
            "Lag Min (s)", "Lag Max (s)", "Lag Avg (s)",
            "Role", "Sync State", "Suspended",
            "Last Hardened LSN", "Last Commit LSN", "Tier"
        };

        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.DarkSlateGray;
        ws.Row(1).Style.Font.FontColor = XLColor.White;

        for (var r = 0; r < data.Count; r++)
        {
            var d = data[r];
            var row = r + 2;
            ws.Cell(row, 1).Value = d.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            ws.Cell(row, 2).Value = d.GroupName;
            ws.Cell(row, 3).Value = d.ReplicaName;
            ws.Cell(row, 4).Value = d.DatabaseName;
            ws.Cell(row, 5).Value = d.SampleCount;
            ws.Cell(row, 6).Value = d.LogSendQueueKbMin;
            ws.Cell(row, 7).Value = d.LogSendQueueKbMax;
            ws.Cell(row, 8).Value = d.LogSendQueueKbAvg;
            ws.Cell(row, 9).Value = d.RedoQueueKbMin;
            ws.Cell(row, 10).Value = d.RedoQueueKbMax;
            ws.Cell(row, 11).Value = d.RedoQueueKbAvg;
            ws.Cell(row, 12).Value = d.LogSendRateMin;
            ws.Cell(row, 13).Value = d.LogSendRateMax;
            ws.Cell(row, 14).Value = d.LogSendRateAvg;
            ws.Cell(row, 15).Value = d.RedoRateMin;
            ws.Cell(row, 16).Value = d.RedoRateMax;
            ws.Cell(row, 17).Value = d.RedoRateAvg;
            ws.Cell(row, 18).Value = (double)d.LogBlockDiffMin;
            ws.Cell(row, 19).Value = (double)d.LogBlockDiffMax;
            ws.Cell(row, 20).Value = d.LogBlockDiffAvg;
            ws.Cell(row, 21).Value = d.SecondaryLagMin;
            ws.Cell(row, 22).Value = d.SecondaryLagMax;
            ws.Cell(row, 23).Value = d.SecondaryLagAvg;
            ws.Cell(row, 24).Value = d.Role;
            ws.Cell(row, 25).Value = d.SyncState;
            ws.Cell(row, 26).Value = d.AnySuspended;
            ws.Cell(row, 27).Value = (double)d.LastHardenedLsn;
            ws.Cell(row, 28).Value = (double)d.LastCommitLsn;
            ws.Cell(row, 29).Value = d.Tier.ToString();
        }

        ws.Columns().AdjustToContents(1, Math.Min(data.Count + 1, 100));
        ws.SheetView.FreezeRows(1);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }
}
