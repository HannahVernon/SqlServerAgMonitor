using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Services;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    private bool _isExiting;
    private WindowLayoutState? _layoutState;
    private readonly ILayoutStateService _layoutService;
    private readonly System.Collections.Generic.HashSet<MonitorTabViewModel> _subscribedVms = new();
    private string? _lastActiveTabTitle;

    public MainWindow()
    {
        InitializeComponent();
        _layoutService = App.Services.GetRequiredService<ILayoutStateService>();
        Opened += OnWindowOpened;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        _layoutState = _layoutService.Load();

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

        _lastActiveTabTitle = newTab?.TabTitle;

        // Rebuild columns for the incoming tab after the visual tree updates.
        // Avalonia's TabControl with ContentTemplate may recreate the DataGrid
        // (orphaning any DataContextChanged handler on the old instance) or may
        // recycle it but not fire DataContextChanged reliably. Post to the
        // dispatcher so we run after the template is applied and DataContext
        // has propagated.
        if (newTab != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var dg = this.GetVisualDescendants()
                    .OfType<DataGrid>()
                    .FirstOrDefault();
                if (dg != null)
                    WireUpDataGrid(dg);
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Restores saved column widths (as proportional Star values) and display indices
    /// for the given tab's DataGrid.
    /// </summary>
    public void RestoreDataGridLayout(DataGrid dataGrid)
    {
        if (_layoutState == null) return;

        var tabVm = dataGrid.DataContext as MonitorTabViewModel;
        var tabKey = tabVm?.TabTitle;
        if (string.IsNullOrEmpty(tabKey)) return;

        if (!_layoutState.TabLayouts.TryGetValue(tabKey, out var tabLayout))
            return;

        // Restore column display order
        foreach (var col in dataGrid.Columns)
        {
            var header = col.Header?.ToString();
            if (header == null) continue;

            if (tabLayout.ColumnDisplayIndices.TryGetValue(header, out var displayIndex)
                && displayIndex >= 0 && displayIndex < dataGrid.Columns.Count)
            {
                col.DisplayIndex = displayIndex;
            }
        }

        // Restore widths as proportional Star values so columns always fit on screen.
        // Saved values are pixel widths — convert to Star weights relative to the
        // narrowest saved column (which becomes 1*).
        var matchedWidths = new System.Collections.Generic.List<(DataGridColumn col, double saved)>();
        foreach (var col in dataGrid.Columns)
        {
            var header = col.Header?.ToString();
            if (header != null && tabLayout.ColumnWidths.TryGetValue(header, out var w) && w > 10)
                matchedWidths.Add((col, w));
        }

        if (matchedWidths.Count > 0)
        {
            var minWidth = matchedWidths.Min(x => x.saved);
            foreach (var (col, saved) in matchedWidths)
            {
                col.Width = new DataGridLength(saved / minWidth, DataGridLengthUnitType.Star);
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

            tabLayout.ColumnWidths[header] = Math.Round(col.ActualWidth);
            tabLayout.ColumnDisplayIndices[header] = col.DisplayIndex;
        }

        _layoutState.TabLayouts[_lastActiveTabTitle] = tabLayout;
    }

    private void SaveLayout()
    {
        // Re-load from disk to pick up any changes saved by other windows (e.g. Statistics)
        var state = _layoutService.Load();

        // Merge in-memory tab column layouts that were tracked during this session
        if (_layoutState != null)
        {
            foreach (var kvp in _layoutState.TabLayouts)
                state.TabLayouts[kvp.Key] = kvp.Value;
        }

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

        _layoutService.Save(state);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isExiting)
        {
            // Minimize to system tray instead of closing
            e.Cancel = true;
            SaveLayout();
            Hide();
            return;
        }

        // True exit via File > Exit
        SaveLayout();
        base.OnClosing(e);
    }

    /// <summary>
    /// Called by the ViewModel's ExitCommand to perform a real application exit.
    /// </summary>
    public void ExitApplication()
    {
        _isExiting = true;
        Close();
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

        // Attach context menu for copy operations (once per DataGrid instance)
        if (dataGrid.ContextMenu == null)
        {
            var copyCell = new MenuItem { Header = "Copy Cell" };
            copyCell.Click += async (_, _) => await CopySelectedCellAsync(dataGrid);

            var copyRow = new MenuItem { Header = "Copy Row" };
            copyRow.Click += async (_, _) => await CopySelectedRowAsync(dataGrid);

            var copyAll = new MenuItem { Header = "Copy All" };
            copyAll.Click += async (_, _) => await CopyAllRowsAsync(dataGrid);

            dataGrid.ContextMenu = new ContextMenu
            {
                Items = { copyCell, copyRow, new Separator(), copyAll }
            };
        }

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

        // All columns use Star sizing so they share available width proportionally
        // and always fit on screen. MinWidth prevents columns from shrinking
        // below readability.

        // Database Name with health dot
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
            Width = new DataGridLength(1.5, DataGridLengthUnitType.Star),
            MinWidth = 120
        });

        // Dynamic: one LSN column per replica (primary first)
        foreach (var col in replicaColumns)
        {
            dataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = col.Header,
                Binding = new Binding($"[{col.Index}]"),
                Width = new DataGridLength(1.3, DataGridLengthUnitType.Star),
                MinWidth = 110
            });
        }

        // Summary columns
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Log Block Diff",
            Binding = new Binding("MaxLogBlockDiff") { StringFormat = "{0:N0}" },
            Width = new DataGridLength(1.0, DataGridLengthUnitType.Star),
            MinWidth = 80
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Lag (sec)",
            Binding = new Binding("SecondaryLagSeconds"),
            Width = new DataGridLength(0.6, DataGridLengthUnitType.Star),
            MinWidth = 55
        });

        // Color-coded sync state
        dataGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Sync State",
            CellTemplate = new FuncDataTemplate<DatabasePivotRow>((row, _) =>
            {
                var text = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(4, 0)
                };
                text.Bind(TextBlock.TextProperty, new Binding("WorstSyncState"));
                text.Bind(TextBlock.ForegroundProperty,
                    new Binding("SyncStateColorHex") { Converter = HealthColorConverter.Instance });
                return text;
            }),
            Width = new DataGridLength(1.0, DataGridLengthUnitType.Star),
            MinWidth = 80
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Suspended",
            Binding = new Binding("SuspendReasonDisplay"),
            Width = new DataGridLength(0.8, DataGridLengthUnitType.Star),
            MinWidth = 65
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Send Queue (KB)",
            Binding = new Binding("SendQueueKb") { StringFormat = "{0:N0}" },
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star),
            MinWidth = 70
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Redo Queue (KB)",
            Binding = new Binding("RedoQueueKb") { StringFormat = "{0:N0}" },
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star),
            MinWidth = 70
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Send Rate (KB/s)",
            Binding = new Binding("SendRateKbPerSec") { StringFormat = "{0:N0}" },
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star),
            MinWidth = 70
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Redo Rate (KB/s)",
            Binding = new Binding("RedoRateKbPerSec") { StringFormat = "{0:N0}" },
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star),
            MinWidth = 70
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

    private static async Task CopySelectedCellAsync(DataGrid dataGrid)
    {
        if (dataGrid.CurrentColumn == null || dataGrid.SelectedItem is not DatabasePivotRow row)
            return;

        var colIndex = dataGrid.Columns.IndexOf(dataGrid.CurrentColumn);
        var text = GetCellText(row, dataGrid.CurrentColumn, colIndex);
        await SetClipboardAsync(dataGrid, text);
    }

    private static async Task CopySelectedRowAsync(DataGrid dataGrid)
    {
        if (dataGrid.SelectedItem is not DatabasePivotRow row) return;

        var parts = dataGrid.Columns
            .Select((col, i) => GetCellText(row, col, i));
        await SetClipboardAsync(dataGrid, string.Join("\t", parts));
    }

    private static async Task CopyAllRowsAsync(DataGrid dataGrid)
    {
        var headers = string.Join("\t", dataGrid.Columns.Select(c => c.Header?.ToString() ?? ""));
        var rows = dataGrid.ItemsSource?.Cast<DatabasePivotRow>()
            .Select(row => string.Join("\t",
                dataGrid.Columns.Select((col, i) => GetCellText(row, col, i))));

        var text = headers + "\n" + string.Join("\n", rows ?? []);
        await SetClipboardAsync(dataGrid, text);
    }

    private static string GetCellText(DatabasePivotRow row, DataGridColumn col, int colIndex)
    {
        if (col is DataGridTextColumn textCol && textCol.Binding is Binding binding)
        {
            var path = binding.Path;
            if (path != null && path.StartsWith("[") && path.EndsWith("]")
                && int.TryParse(path.AsSpan(1, path.Length - 2), out var idx))
            {
                return row[idx];
            }

            return path switch
            {
                "DatabaseName" => row.DatabaseName,
                "MaxLogBlockDiff" => row.MaxLogBlockDiff.ToString("N0"),
                "SecondaryLagSeconds" => row.SecondaryLagSeconds.ToString(),
                "WorstSyncState" => row.WorstSyncState,
                "SuspendReasonDisplay" => row.SuspendReasonDisplay,
                "SendQueueKb" => row.SendQueueKb.ToString("N0"),
                "RedoQueueKb" => row.RedoQueueKb.ToString("N0"),
                "SendRateKbPerSec" => row.SendRateKbPerSec.ToString("N0"),
                "RedoRateKbPerSec" => row.RedoRateKbPerSec.ToString("N0"),
                _ => ""
            };
        }

        // Template columns (Database name, Sync State)
        if (col.Header?.ToString() == "Database") return row.DatabaseName;
        if (col.Header?.ToString() == "Sync State") return row.WorstSyncState;
        return "";
    }

    private static async Task SetClipboardAsync(DataGrid dataGrid, string text)
    {
        var topLevel = TopLevel.GetTopLevel(dataGrid);
        if (topLevel?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}