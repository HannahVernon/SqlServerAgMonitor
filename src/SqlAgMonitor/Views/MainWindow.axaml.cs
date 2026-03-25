using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using SqlAgMonitor.Services;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    private bool _isExiting;
    private WindowLayoutState? _layoutState;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        _layoutState = LayoutStateService.Load();

        if (_layoutState.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            if (_layoutState.WindowWidth.HasValue && _layoutState.WindowHeight.HasValue)
            {
                Width = _layoutState.WindowWidth.Value;
                Height = _layoutState.WindowHeight.Value;
            }

            if (_layoutState.WindowX.HasValue && _layoutState.WindowY.HasValue)
            {
                Position = new PixelPoint(
                    (int)_layoutState.WindowX.Value,
                    (int)_layoutState.WindowY.Value);
            }
        }
    }

    /// <summary>
    /// Called by TabControl content template when the DataGrid is loaded.
    /// Restores saved column widths and display indices.
    /// </summary>
    public void RestoreDataGridLayout(DataGrid dataGrid)
    {
        if (_layoutState == null) return;

        foreach (var col in dataGrid.Columns)
        {
            var header = col.Header?.ToString();
            if (header == null) continue;

            if (_layoutState.ColumnWidths.TryGetValue(header, out var width) && width > 10)
            {
                col.Width = new DataGridLength(width);
            }

            if (_layoutState.ColumnDisplayIndices.TryGetValue(header, out var displayIndex)
                && displayIndex >= 0 && displayIndex < dataGrid.Columns.Count)
            {
                col.DisplayIndex = displayIndex;
            }
        }
    }

    private void SaveLayout()
    {
        var state = _layoutState ?? new WindowLayoutState();

        state.IsMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            state.WindowX = Position.X;
            state.WindowY = Position.Y;
            state.WindowWidth = Bounds.Width;
            state.WindowHeight = Bounds.Height;
        }

        // Save splitter position from the first Grid with a GridSplitter
        var grid = this.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(g => g.RowDefinitions.Count == 3
                && g.Children.OfType<GridSplitter>().Any());

        if (grid != null && grid.RowDefinitions.Count >= 3)
        {
            var totalHeight = grid.RowDefinitions[0].ActualHeight + grid.RowDefinitions[2].ActualHeight;
            if (totalHeight > 0)
            {
                state.SplitterTopProportion = grid.RowDefinitions[0].ActualHeight / totalHeight;
            }
        }

        // Save DataGrid column widths and display indices
        var dataGrid = this.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();
        if (dataGrid != null)
        {
            state.ColumnWidths.Clear();
            state.ColumnDisplayIndices.Clear();
            foreach (var col in dataGrid.Columns)
            {
                var header = col.Header?.ToString();
                if (header == null) continue;

                state.ColumnWidths[header] = col.ActualWidth;
                state.ColumnDisplayIndices[header] = col.DisplayIndex;
            }
        }

        LayoutStateService.Save(state);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveLayout();

        if (!_isExiting && DataContext is MainWindowViewModel vm)
        {
            _isExiting = true;
            vm.ExitCommand.Execute().Subscribe();
        }
        base.OnClosing(e);
    }

    private void SplitGrid_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_layoutState == null || sender is not Grid grid) return;
        if (grid.RowDefinitions.Count < 3) return;

        var proportion = _layoutState.SplitterTopProportion;
        if (proportion is > 0.05 and < 0.95)
        {
            grid.RowDefinitions[0].Height = new GridLength(proportion, GridUnitType.Star);
            grid.RowDefinitions[2].Height = new GridLength(1.0 - proportion, GridUnitType.Star);
        }
    }

    private void DataGrid_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is DataGrid dataGrid)
        {
            RestoreDataGridLayout(dataGrid);
        }
    }
}