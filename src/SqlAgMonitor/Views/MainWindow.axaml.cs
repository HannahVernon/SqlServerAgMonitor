using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using SqlAgMonitor.Core.Models;
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
        if (sender is not DataGrid dataGrid) return;

        // Get the tab's view model
        var tabVm = dataGrid.DataContext as MonitorTabViewModel;
        if (tabVm == null) return;

        BuildPivotColumns(dataGrid, tabVm);
        RestoreDataGridLayout(dataGrid);

        // Subscribe to future column changes (e.g. replicas added/removed)
        tabVm.ReplicaColumnsChanged += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BuildPivotColumns(dataGrid, tabVm);
                RestoreDataGridLayout(dataGrid);
            });
        };
    }

    private static void BuildPivotColumns(DataGrid dataGrid, MonitorTabViewModel tabVm)
    {
        var replicaColumns = tabVm.ReplicaColumns;
        if (replicaColumns.Count == 0 && dataGrid.Columns.Count > 0)
            return; // Don't clear if we haven't received data yet

        dataGrid.Columns.Clear();

        // Fixed: Database Name with health dot
        dataGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Database",
            CellTemplate = new FuncDataTemplate<DatabasePivotRow>((row, _) =>
            {
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0)
                };

                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    VerticalAlignment = VerticalAlignment.Center
                };
                dot.Bind(Ellipse.FillProperty,
                    new Binding("HealthColorHex")
                    {
                        Converter = HealthColorConverter.Instance
                    });

                var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                text.Bind(TextBlock.TextProperty, new Binding("DatabaseName"));

                panel.Children.Add(dot);
                panel.Children.Add(text);
                return panel;
            }),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        // Dynamic: one LSN column per replica (primary first)
        foreach (var col in replicaColumns)
        {
            dataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = col.Header,
                Binding = new Binding($"[{col.Index}]"),
                Width = new DataGridLength(160)
            });
        }

        // Fixed summary columns
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Max LSN Diff",
            Binding = new Binding("MaxLsnDiff"),
            Width = new DataGridLength(100)
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Sync State",
            Binding = new Binding("WorstSyncState"),
            Width = new DataGridLength(120)
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Suspended",
            Binding = new Binding("AnySuspended"),
            Width = new DataGridLength(80)
        });
    }

    /// <summary>Converts a hex color string to an IBrush for the health dot.</summary>
    private sealed class HealthColorConverter : Avalonia.Data.Converters.IValueConverter
    {
        public static readonly HealthColorConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter,
            System.Globalization.CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrEmpty(hex))
                return new SolidColorBrush(Color.Parse(hex));
            return new SolidColorBrush(Color.Parse("#9E9E9E"));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter,
            System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}