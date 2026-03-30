using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using SkiaSharp;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.History;

namespace SqlAgMonitor.ViewModels;

public class StatisticsViewModel : ViewModelBase
{
    private string _selectedTimeRange = "24 hours";
    private DateTime? _customFrom = DateTime.UtcNow.AddDays(-1);
    private DateTime? _customUntil = DateTime.UtcNow;
    private string? _selectedGroup;
    private string? _selectedReplica;
    private string? _selectedDatabase;
    private bool _isLoading;
    private string _statusText = "Select a time range to load data.";
    private bool _isCustomRange;
    private SnapshotTier _activeTier;
    private bool _initializing;
    private bool _autoRefresh;
    private IDisposable? _autoRefreshSubscription;

    public static string[] TimeRangeOptions { get; } =
        ["24 hours", "7 days", "30 days", "90 days", "180 days", "365 days", "Custom"];

    public string SelectedTimeRange
    {
        get => _selectedTimeRange;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTimeRange, value);
            IsCustomRange = value == "Custom";
            if (!IsCustomRange && !_initializing)
                _ = LoadDataAsync();
        }
    }

    public DateTime? CustomFrom
    {
        get => _customFrom;
        set => this.RaiseAndSetIfChanged(ref _customFrom, value);
    }

    public DateTime? CustomUntil
    {
        get => _customUntil;
        set => this.RaiseAndSetIfChanged(ref _customUntil, value);
    }

    public bool IsCustomRange
    {
        get => _isCustomRange;
        set => this.RaiseAndSetIfChanged(ref _isCustomRange, value);
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoRefresh, value);
            ConfigureAutoRefresh(value);
        }
    }

    public string? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedGroup, value);
            if (!_initializing)
            {
                _ = RefreshCascadingFiltersAsync(groupChanged: true);
                _ = LoadDataAsync();
            }
        }
    }

    public string? SelectedReplica
    {
        get => _selectedReplica;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedReplica, value);
            if (!_initializing)
            {
                _ = RefreshCascadingFiltersAsync(groupChanged: false);
                _ = LoadDataAsync();
            }
        }
    }

    public string? SelectedDatabase
    {
        get => _selectedDatabase;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedDatabase, value);
            if (!_initializing) _ = LoadDataAsync();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    // Filter dropdown sources
    public ObservableCollection<string> GroupNames { get; } = new() { "(All)" };
    public ObservableCollection<string> ReplicaNames { get; } = new() { "(All)" };
    public ObservableCollection<string> DatabaseNames { get; } = new() { "(All)" };

    // Summary grid data
    public ObservableCollection<SnapshotDataPoint> SummaryRows { get; } = new();

    // Chart series
    private ISeries[] _sendQueueSeries = [];
    private ISeries[] _redoQueueSeries = [];
    private ISeries[] _lagSeries = [];
    private ISeries[] _logBlockDiffSeries = [];

    public ISeries[] SendQueueSeries
    {
        get => _sendQueueSeries;
        set => this.RaiseAndSetIfChanged(ref _sendQueueSeries, value);
    }

    public ISeries[] RedoQueueSeries
    {
        get => _redoQueueSeries;
        set => this.RaiseAndSetIfChanged(ref _redoQueueSeries, value);
    }

    public ISeries[] LagSeries
    {
        get => _lagSeries;
        set => this.RaiseAndSetIfChanged(ref _lagSeries, value);
    }

    public ISeries[] LogBlockDiffSeries
    {
        get => _logBlockDiffSeries;
        set => this.RaiseAndSetIfChanged(ref _logBlockDiffSeries, value);
    }

    // Shared X-axis for all charts (time axis)
    public Axis[] XAxes { get; } =
    [
        new DateTimeAxis(TimeSpan.FromHours(1), date => date.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture))
        {
            LabelsRotation = -45,
            TextSize = 11,
            LabelsPaint = new SolidColorPaint(SKColors.LightGray)
        }
    ];

    public Axis[] SendQueueYAxes { get; } = [CreateYAxis("KB")];
    public Axis[] RedoQueueYAxes { get; } = [CreateYAxis("KB")];
    public Axis[] LagYAxes { get; } = [CreateYAxis("Seconds")];
    public Axis[] LogBlockDiffYAxes { get; } = [CreateYAxis("Diff")];

    public ReactiveCommand<Unit, Unit> LoadCommand { get; }
    public ReactiveCommand<string, Unit> ExportCommand { get; }

    // Holds the loaded data for export
    private IReadOnlyList<SnapshotDataPoint> _loadedData = Array.Empty<SnapshotDataPoint>();

    public StatisticsViewModel()
    {
        LoadCommand = ReactiveCommand.CreateFromTask(LoadDataAsync);
        ExportCommand = ReactiveCommand.CreateFromTask<string>(ExportToExcelAsync);
    }

    public async Task InitializeAsync()
    {
        var svc = App.Services?.GetService<IEventHistoryService>();
        if (svc == null)
        {
            StatusText = "History service unavailable.";
            return;
        }

        _initializing = true;
        try
        {
            var filters = await svc.GetSnapshotFiltersAsync();

            GroupNames.Clear();
            GroupNames.Add("(All)");
            foreach (var g in filters.GroupNames) GroupNames.Add(g);

            ReplicaNames.Clear();
            ReplicaNames.Add("(All)");
            foreach (var r in filters.ReplicaNames) ReplicaNames.Add(r);

            DatabaseNames.Clear();
            DatabaseNames.Add("(All)");
            foreach (var d in filters.DatabaseNames) DatabaseNames.Add(d);

            _selectedGroup = "(All)";
            _selectedReplica = "(All)";
            _selectedDatabase = "(All)";
            this.RaisePropertyChanged(nameof(SelectedGroup));
            this.RaisePropertyChanged(nameof(SelectedReplica));
            this.RaisePropertyChanged(nameof(SelectedDatabase));
        }
        finally
        {
            _initializing = false;
        }

        await LoadDataAsync();
    }

    private async Task RefreshCascadingFiltersAsync(bool groupChanged)
    {
        var svc = App.Services?.GetService<IEventHistoryService>();
        if (svc == null) return;

        var group = _selectedGroup == "(All)" ? null : _selectedGroup;
        var replica = _selectedReplica == "(All)" ? null : _selectedReplica;

        var filters = await svc.GetSnapshotFiltersAsync(group, groupChanged ? null : replica);

        _initializing = true;
        try
        {
            // Refresh replicas when group changes
            if (groupChanged)
            {
                ReplicaNames.Clear();
                ReplicaNames.Add("(All)");
                foreach (var r in filters.ReplicaNames) ReplicaNames.Add(r);

                if (!ReplicaNames.Contains(_selectedReplica ?? ""))
                {
                    _selectedReplica = "(All)";
                    this.RaisePropertyChanged(nameof(SelectedReplica));
                }
            }

            // Always refresh databases based on current group + replica
            DatabaseNames.Clear();
            DatabaseNames.Add("(All)");
            foreach (var d in filters.DatabaseNames) DatabaseNames.Add(d);

            if (!DatabaseNames.Contains(_selectedDatabase ?? ""))
            {
                _selectedDatabase = "(All)";
                this.RaisePropertyChanged(nameof(SelectedDatabase));
            }
        }
        finally
        {
            _initializing = false;
        }
    }

    private (DateTimeOffset since, DateTimeOffset until) GetTimeRange()
    {
        var now = DateTimeOffset.UtcNow;
        return SelectedTimeRange switch
        {
            "24 hours" => (now.AddHours(-24), now),
            "7 days" => (now.AddDays(-7), now),
            "30 days" => (now.AddDays(-30), now),
            "90 days" => (now.AddDays(-90), now),
            "180 days" => (now.AddDays(-180), now),
            "365 days" => (now.AddDays(-365), now),
            "Custom" => (
                new DateTimeOffset(CustomFrom ?? DateTime.UtcNow.AddDays(-1), TimeSpan.Zero),
                new DateTimeOffset(CustomUntil ?? DateTime.UtcNow, TimeSpan.Zero)),
            _ => (now.AddHours(-24), now)
        };
    }

    public async Task LoadDataAsync(CancellationToken cancellationToken = default)
    {
        var svc = App.Services?.GetService<IEventHistoryService>();
        if (svc == null) return;

        IsLoading = true;
        StatusText = "Loading...";

        try
        {
            var (since, until) = GetTimeRange();
            var group = _selectedGroup == "(All)" ? null : _selectedGroup;
            var replica = _selectedReplica == "(All)" ? null : _selectedReplica;
            var database = _selectedDatabase == "(All)" ? null : _selectedDatabase;

            var data = await svc.GetSnapshotDataAsync(since, until, group, replica, database, cancellationToken);
            _loadedData = data;

            if (data.Count > 0)
                _activeTier = data[0].Tier;

            // Update summary grid
            SummaryRows.Clear();
            foreach (var row in data)
                SummaryRows.Add(row);

            // Build chart series grouped by replica+database
            BuildChartSeries(data);

            var tierLabel = data.Count > 0 ? data[0].Tier.ToString().ToLowerInvariant() : "n/a";
            StatusText = $"{data.Count} data points loaded ({tierLabel} tier)";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void BuildChartSeries(IReadOnlyList<SnapshotDataPoint> data)
    {
        // Group by replica + database for multi-line charts
        var groups = data
            .GroupBy(d => $"{d.ReplicaName} / {d.DatabaseName}")
            .ToList();

        var colors = new[]
        {
            SKColors.CornflowerBlue, SKColors.LightGreen, SKColors.Orange,
            SKColors.Orchid, SKColors.Gold, SKColors.Salmon,
            SKColors.Turquoise, SKColors.LightCoral
        };

        SendQueueSeries = BuildLineSeries(groups, colors, d => d.LogSendQueueKbAvg);
        RedoQueueSeries = BuildLineSeries(groups, colors, d => d.RedoQueueKbAvg);
        LagSeries = BuildLineSeries(groups, colors, d => d.SecondaryLagAvg);
        LogBlockDiffSeries = BuildLineSeries(groups, colors, d => d.LogBlockDiffAvg);
    }

    private static ISeries[] BuildLineSeries(
        List<IGrouping<string, SnapshotDataPoint>> groups,
        SKColor[] colors,
        Func<SnapshotDataPoint, double> valueSelector)
    {
        var series = new List<ISeries>();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var color = colors[i % colors.Length];

            var values = g.Select(d => new DateTimePoint(
                d.Timestamp.ToLocalTime().DateTime,
                valueSelector(d))).ToArray();

            series.Add(new LineSeries<DateTimePoint>
            {
                Name = g.Key,
                Values = values,
                GeometrySize = 0,
                LineSmoothness = 0.3,
                Stroke = new SolidColorPaint(color, 2),
                Fill = null,
                MiniatureShapeSize = 8,
                YToolTipLabelFormatter = point =>
                    $"{point.Model!.DateTime:HH:mm}  {point.Model.Value:N1}"
            });
        }
        return series.ToArray();
    }

    private async Task ExportToExcelAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        if (_loadedData.Count == 0)
        {
            StatusText = "No data to export.";
            return;
        }

        IsLoading = true;
        StatusText = "Exporting to Excel...";

        try
        {
            await Task.Run(() =>
            {
                using var wb = new XLWorkbook();
                var ws = wb.Worksheets.Add("Statistics");

                // Header row
                var headers = new[]
                {
                    "Timestamp", "Group", "Replica", "Database", "Samples",
                    "Send Queue Min (KB)", "Send Queue Max (KB)", "Send Queue Avg (KB)",
                    "Redo Queue Min (KB)", "Redo Queue Max (KB)", "Redo Queue Avg (KB)",
                    "Send Rate Min (KB/s)", "Send Rate Max (KB/s)", "Send Rate Avg (KB/s)",
                    "Redo Rate Min (KB/s)", "Redo Rate Max (KB/s)", "Redo Rate Avg (KB/s)",
                    "Log Block Diff Min", "Log Block Diff Max", "Log Block Diff Avg",
                    "Lag Min (s)", "Lag Max (s)", "Lag Avg (s)",
                    "Role", "Sync State", "Suspended",
                    "Last Hardened LSN", "Last Commit LSN", "Tier"
                };

                for (var c = 0; c < headers.Length; c++)
                    ws.Cell(1, c + 1).Value = headers[c];

                ws.Row(1).Style.Font.Bold = true;
                ws.Row(1).Style.Fill.BackgroundColor = XLColor.DarkSlateGray;
                ws.Row(1).Style.Font.FontColor = XLColor.White;

                for (var r = 0; r < _loadedData.Count; r++)
                {
                    var d = _loadedData[r];
                    var row = r + 2;
                    ws.Cell(row, 1).Value = d.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    ws.Cell(row, 2).Value = d.GroupName;
                    ws.Cell(row, 3).Value = d.ReplicaName;
                    ws.Cell(row, 4).Value = d.DatabaseName;
                    ws.Cell(row, 5).Value = d.SampleCount;
                    ws.Cell(row, 6).Value = d.LogSendQueueKbMin;
                    ws.Cell(row, 7).Value = d.LogSendQueueKbMax;
                    ws.Cell(row, 8).Value = d.LogSendQueueKbAvg;
                    ws.Cell(row, 9).Value = d.RedoQueueKbMin;
                    ws.Cell(row, 10).Value = d.RedoQueueKbMax;
                    ws.Cell(row, 11).Value = d.RedoQueueKbAvg;
                    ws.Cell(row, 12).Value = d.LogSendRateMin;
                    ws.Cell(row, 13).Value = d.LogSendRateMax;
                    ws.Cell(row, 14).Value = d.LogSendRateAvg;
                    ws.Cell(row, 15).Value = d.RedoRateMin;
                    ws.Cell(row, 16).Value = d.RedoRateMax;
                    ws.Cell(row, 17).Value = d.RedoRateAvg;
                    ws.Cell(row, 18).Value = (double)d.LogBlockDiffMin;
                    ws.Cell(row, 19).Value = (double)d.LogBlockDiffMax;
                    ws.Cell(row, 20).Value = d.LogBlockDiffAvg;
                    ws.Cell(row, 21).Value = d.SecondaryLagMin;
                    ws.Cell(row, 22).Value = d.SecondaryLagMax;
                    ws.Cell(row, 23).Value = d.SecondaryLagAvg;
                    ws.Cell(row, 24).Value = d.Role;
                    ws.Cell(row, 25).Value = d.SyncState;
                    ws.Cell(row, 26).Value = d.AnySuspended;
                    ws.Cell(row, 27).Value = (double)d.LastHardenedLsn;
                    ws.Cell(row, 28).Value = (double)d.LastCommitLsn;
                    ws.Cell(row, 29).Value = d.Tier.ToString();
                }

                ws.Columns().AdjustToContents(1, Math.Min(_loadedData.Count + 1, 100));
                ws.SheetView.FreezeRows(1);

                wb.SaveAs(outputPath);
            }, cancellationToken);

            StatusText = $"Exported {_loadedData.Count} rows to {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static Axis CreateYAxis(string title) => new()
    {
        Name = title,
        NameTextSize = 12,
        TextSize = 11,
        NamePaint = new SolidColorPaint(SKColors.LightGray),
        LabelsPaint = new SolidColorPaint(SKColors.LightGray),
        MinLimit = 0
    };

    private void ConfigureAutoRefresh(bool enabled)
    {
        _autoRefreshSubscription?.Dispose();
        _autoRefreshSubscription = null;

        if (!enabled) return;

        var configService = App.Services?.GetService<IConfigurationService>();
        var intervalSeconds = configService?.Load().GlobalPollingIntervalSeconds ?? 16;

        _autoRefreshSubscription = Observable.Interval(TimeSpan.FromSeconds(intervalSeconds))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(_ => !_isLoading)
            .SelectMany(_ => Observable.FromAsync(LoadDataAsync))
            .Subscribe();
    }

    public void Dispose()
    {
        _autoRefreshSubscription?.Dispose();
        _autoRefreshSubscription = null;
    }
}
