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

    private void OnWindowOpened(object? sender, EventArgs e)
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

        if (DataContext is StatisticsViewModel vm)
        {
            _ = vm.InitializeAsync(state.StatsState);
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

        if (DataContext is StatisticsViewModel vm)
        {
            state.StatsState = vm.SaveState();
            vm.Dispose();
        }

        _layoutService.Save(state);
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
