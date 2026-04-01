using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using SqlAgMonitor.Helpers;
using SqlAgMonitor.Services;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class StatisticsWindow : Window
{
    private readonly ILayoutStateService _layoutService;

    public StatisticsWindow()
    {
        InitializeComponent();
        _layoutService = App.Services.GetRequiredService<ILayoutStateService>();
        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        var state = _layoutService.Load();

        if (state.StatsWindowWidth.HasValue && state.StatsWindowHeight.HasValue)
        {
            Width = state.StatsWindowWidth.Value;
            Height = state.StatsWindowHeight.Value;
        }

        if (state.StatsWindowX.HasValue && state.StatsWindowY.HasValue)
        {
            Position = new PixelPoint(
                (int)state.StatsWindowX.Value,
                (int)state.StatsWindowY.Value);
        }

        RestoreColumnWidths(state);
        DataGridAutoFitHelper.Attach(SummaryGrid);

        if (DataContext is StatisticsViewModel vm)
        {
            try
            {
                await vm.InitializeAsync(state.StatsState);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Statistics initialization failed: {ex.Message}");
            }
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        var state = _layoutService.Load();

        if (WindowState == WindowState.Normal)
        {
            state.StatsWindowX = Position.X;
            state.StatsWindowY = Position.Y;
            state.StatsWindowWidth = Bounds.Width;
            state.StatsWindowHeight = Bounds.Height;
        }

        SaveColumnWidths(state);

        if (DataContext is StatisticsViewModel vm)
        {
            state.StatsState = vm.SaveState();

            // LiveCharts' SkiaSharp render loop runs on the composition thread
            // and may be mid-frame when we close. To prevent a native crash
            // (AccessViolation in sk_font_get_size/sk_font_set_edging):
            //
            // 1. Remove all CartesianChart controls from the visual tree so the
            //    composition thread has nothing to render on the next frame.
            // 2. Null the DataContext to detach bindings.
            // 3. Defer VM.Dispose() to a low-priority callback so at least one
            //    render cycle completes with the charts removed.
            var charts = this.GetVisualDescendants().OfType<CartesianChart>().ToList();
            foreach (var chart in charts)
            {
                if (chart.Parent is Panel panel)
                    panel.Children.Remove(chart);
                else if (chart.Parent is Decorator decorator)
                    decorator.Child = null;
            }

            DataContext = null;

            Dispatcher.UIThread.Post(() => vm.Dispose(), DispatcherPriority.Background);
        }

        _layoutService.Save(state);
    }

    private void SaveColumnWidths(WindowLayoutState state)
    {
        if (SummaryGrid.Columns.Count == 0) return;

        var layout = new TabGridLayout();
        foreach (var col in SummaryGrid.Columns)
        {
            var header = col.Header?.ToString();
            if (header == null) continue;
            layout.ColumnWidths[header] = Math.Round(col.ActualWidth);
        }
        state.StatsGridLayout = layout;
    }

    private void RestoreColumnWidths(WindowLayoutState state)
    {
        if (state.StatsGridLayout == null) return;

        foreach (var col in SummaryGrid.Columns)
        {
            var header = col.Header?.ToString();
            if (header != null
                && state.StatsGridLayout.ColumnWidths.TryGetValue(header, out var w)
                && w > 10)
            {
                col.Width = new DataGridLength(w);
            }
        }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not StatisticsViewModel vm) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Statistics to Excel",
                SuggestedFileName = $"ag-statistics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Excel Workbook") { Patterns = new[] { "*.xlsx" } }
                }
            });

            if (file == null) return;

            var path = file.TryGetLocalPath();
            if (path != null)
            {
                vm.ExportCommand.Execute(path).Subscribe();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Export failed: {ex.Message}");
        }
    }
}
