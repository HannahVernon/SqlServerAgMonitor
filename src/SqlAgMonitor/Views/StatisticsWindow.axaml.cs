using System;
using System.IO;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
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
            vm.Dispose();
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
