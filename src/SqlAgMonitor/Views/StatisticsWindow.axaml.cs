using System;
using System.IO;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SqlAgMonitor.Services;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class StatisticsWindow : Window
{
    public StatisticsWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        var state = LayoutStateService.Load();

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
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        var state = LayoutStateService.Load();

        if (WindowState == WindowState.Normal)
        {
            state.StatsWindowX = Position.X;
            state.StatsWindowY = Position.Y;
            state.StatsWindowWidth = Bounds.Width;
            state.StatsWindowHeight = Bounds.Height;
            LayoutStateService.Save(state);
        }

        (DataContext as StatisticsViewModel)?.Dispose();
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
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
}
