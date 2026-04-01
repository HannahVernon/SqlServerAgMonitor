using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SqlAgMonitor.Helpers;

/// <summary>
/// Enables double-click-to-auto-fit on DataGrid column header separators.
/// Double-clicking the right edge of a column header auto-sizes that column
/// to fit its content. Double-clicking the left edge auto-sizes the column
/// to the left.
/// </summary>
internal static class DataGridAutoFitHelper
{
    private const double GripZonePixels = 8;

    /// <summary>
    /// Attaches the double-click auto-fit behavior to the given DataGrid.
    /// Safe to call multiple times — duplicate handlers are prevented.
    /// </summary>
    public static void Attach(DataGrid dataGrid)
    {
        dataGrid.DoubleTapped -= OnDoubleTapped;
        dataGrid.DoubleTapped += OnDoubleTapped;
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid dataGrid) return;

        var source = e.Source as Visual;
        if (source == null) return;

        var header = source.FindAncestorOfType<DataGridColumnHeader>();
        if (header == null) return;

        // Match the header to its column via header text
        var headerText = header.Content?.ToString();
        if (string.IsNullOrEmpty(headerText)) return;

        var matchedColumn = dataGrid.Columns
            .FirstOrDefault(c => c.Header?.ToString() == headerText);
        if (matchedColumn == null) return;

        var position = e.GetPosition(header);
        DataGridColumn? columnToFit = null;

        if (position.X >= header.Bounds.Width - GripZonePixels)
        {
            columnToFit = matchedColumn;
        }
        else if (position.X <= GripZonePixels)
        {
            var prevDisplayIndex = matchedColumn.DisplayIndex - 1;
            if (prevDisplayIndex >= 0)
            {
                columnToFit = dataGrid.Columns
                    .FirstOrDefault(c => c.DisplayIndex == prevDisplayIndex);
            }
        }

        if (columnToFit == null) return;

        // Temporarily set to Auto so Avalonia measures content, then lock to pixels
        columnToFit.Width = DataGridLength.Auto;
        Dispatcher.UIThread.Post(() =>
        {
            var measured = columnToFit.ActualWidth;
            var final = Math.Max(columnToFit.MinWidth, Math.Ceiling(measured));
            if (final > 10)
                columnToFit.Width = new DataGridLength(final);
        }, DispatcherPriority.Render);

        e.Handled = true;
    }
}
