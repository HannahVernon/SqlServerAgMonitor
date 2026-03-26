using System;
using System.Linq;
using System.Reactive.Linq;
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
using ReactiveUI;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Services;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    private bool _isExiting;
    private WindowLayoutState? _layoutState;
    private readonly System.Collections.Generic.HashSet<MonitorTabViewModel> _subscribedVms = new();
    private string? _lastActiveTabTitle;

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

        // Track tab switches to save/restore per-tab column layouts
        if (DataContext is MainWindowViewModel vm)
        {
            vm.WhenAnyValue(x => x.SelectedTab)
                .Subscribe(OnSelectedTabChanged);
        }
    }

    private void OnSelectedTabChanged(MonitorTabViewModel? newTab)
    {
        // Save outgoing tab's column state
        SaveCurrentTabColumnState();

        // Restore incoming tab's column state (columns are rebuilt in WireUpDataGrid,
        // but we also need to apply saved widths/order after that rebuild completes)
        _lastActiveTabTitle = newTab?.TabTitle;
    }

    /// <summary>
    /// Restores saved column widths and display indices for the given tab's DataGrid.
    /// </summary>
    public void RestoreDataGridLayout(DataGrid dataGrid)
    {
        if (_layoutState == null) return;

        var tabVm = dataGrid.DataContext as MonitorTabViewModel;
        var tabKey = tabVm?.TabTitle;
        if (string.IsNullOrEmpty(tabKey)) return;

        if (!_layoutState.TabLayouts.TryGetValue(tabKey, out var tabLayout))
            return;

        foreach (var col in dataGrid.Columns)
        {
            var header = col.Header?.ToString();
            if (header == null) continue;

            if (tabLayout.ColumnWidths.TryGetValue(header, out var width) && width > 10)
            {
                col.Width = new DataGridLength(width);
            }

            if (tabLayout.ColumnDisplayIndices.TryGetValue(header, out var displayIndex)
                && displayIndex >= 0 && displayIndex < dataGrid.Columns.Count)
            {
                col.DisplayIndex = displayIndex;
            }
        }
    }

    /// <summary>Saves the current tab's DataGrid column state to the in-memory layout.</summary>
    private void SaveCurrentTabColumnState()
    {
        if (_layoutState == null || string.IsNullOrEmpty(_lastActiveTabTitle)) return;

        var dataGrid = this.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();
        if (dataGrid == null || dataGrid.Columns.Count == 0) return;

        var tabLayout = new TabGridLayout();
        foreach (var col in dataGrid.Columns)
        {
            var header = col.Header?.ToString();
            if (header == null) continue;

            tabLayout.ColumnWidths[header] = col.ActualWidth;
            tabLayout.ColumnDisplayIndices[header] = col.DisplayIndex;
        }

        _layoutState.TabLayouts[_lastActiveTabTitle] = tabLayout;
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

        // Save the currently active tab's column state
        SaveCurrentTabColumnState();

        // Clear obsolete global column properties
#pragma warning disable CS0618 // Intentional: clearing legacy properties during migration
        state.ColumnWidths = null;
        state.ColumnDisplayIndices = null;
#pragma warning restore CS0618

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

        // Wire up columns for the current DataContext
        WireUpDataGrid(dataGrid);

        // Handle tab switches: DataContext changes when the TabControl recycles the DataGrid
        dataGrid.DataContextChanged += (s, _) =>
        {
            if (s is DataGrid dg)
                WireUpDataGrid(dg);
        };
    }

    private void WireUpDataGrid(DataGrid dataGrid)
    {
        var tabVm = dataGrid.DataContext as MonitorTabViewModel;
        if (tabVm == null) return;

        // Track the active tab for save-on-switch
        _lastActiveTabTitle = tabVm.TabTitle;

        BuildPivotColumns(dataGrid, tabVm);
        RestoreDataGridLayout(dataGrid);

        // Subscribe to future column changes only once per VM
        if (!_subscribedVms.Add(tabVm)) return;

        tabVm.ReplicaColumnsChanged += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Find the DataGrid currently displaying this VM
                var activeDg = this.GetVisualDescendants()
                    .OfType<DataGrid>()
                    .FirstOrDefault(dg => dg.DataContext == tabVm);
                if (activeDg != null)
                {
                    BuildPivotColumns(activeDg, tabVm);
                    RestoreDataGridLayout(activeDg);
                }
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
            Header = "Log Block Diff",
            Binding = new Binding("MaxLogBlockDiff") { StringFormat = "{0:N0}" },
            Width = new DataGridLength(120)
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Lag (sec)",
            Binding = new Binding("SecondaryLagSeconds"),
            Width = new DataGridLength(80)
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