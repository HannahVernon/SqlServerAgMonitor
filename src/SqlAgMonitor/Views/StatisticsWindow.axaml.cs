using System;
using System.IO;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class StatisticsWindow : Window
{
    public StatisticsWindow()
    {
        InitializeComponent();
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
