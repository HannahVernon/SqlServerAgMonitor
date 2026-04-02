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
using ReactiveUI;
using SkiaSharp;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Services;

namespace SqlAgMonitor.ViewModels;

public class StatisticsViewModel : ViewModelBase
{
    private readonly ISnapshotQueryService _snapshotQuery;
    private readonly IConfigurationService _configService;
    private string _selectedTimeRange = "24 hours";
    private DateTime? _customFrom = DateTime.UtcNow.AddDays(-1);
    private DateTime? _customUntil = DateTime.UtcNow;
    private string? _selectedGroup;
    private string? _selectedReplica;
    private string? _selectedDatabase;
    private bool _isLoading = true;
    private string _statusText = "Select a time range to load data.";
    private bool _isCustomRange;
    private SnapshotTier _activeTier;
    private bool _initializing;
    private bool _autoRefresh;
    private IDisposable? _autoRefreshSubscription;
    private CancellationTokenSource? _loadCts;

    public static string[] TimeRangeOptions { get; } =
        ["15 minutes", "1 hour", "4 hours", "8 hours", "24 hours", "7 days", "30 days", "90 days", "180 days", "365 days", "Custom"];

    public string SelectedTimeRange
    {
        get => _selectedTimeRange;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTimeRange, value);
            IsCustomRange = value == "Custom";
            this.RaisePropertyChanged(nameof(IsAutoRefreshEnabled));

            // Auto-refresh is only practical for short time ranges.
            // Disable it for ranges >= 8 hours to avoid repeatedly loading
            // large data sets.
            if (!_initializing && !IsAutoRefreshAllowed(value))
                AutoRefresh = false;

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

    public bool IsAutoRefreshEnabled => IsAutoRefreshAllowed(_selectedTimeRange);

    private static bool IsAutoRefreshAllowed(string timeRange)
        => timeRange is "15 minutes" or "1 hour" or "4 hours";

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

    // Summary grid data — settable so large result sets can be swapped in
    // with a single PropertyChanged notification instead of per-item Add().
    private ObservableCollection<SnapshotDataPoint> _summaryRows = new();
    public ObservableCollection<SnapshotDataPoint> SummaryRows
    {
        get => _summaryRows;
        set => this.RaiseAndSetIfChanged(ref _summaryRows, value);
    }

    // Chart series
    private ISeries[] _sendQueueSeries = [];
    private ISeries[] _redoQueueSeries = [];
    private ISeries[] _lagSeries = [];
    private ISeries[] _logBlockDiffSeries = [];

    // Retain previous-generation series so the GC cannot finalize their
    // SkiaSharp paint/font handles while the composition-thread render loop
    // might still be mid-frame drawing them. Overwritten on the next refresh.
    private ISeries[] _retainedSend = [];
    private ISeries[] _retainedRedo = [];
    private ISeries[] _retainedLag = [];
    private ISeries[] _retainedLogDiff = [];
    private Axis[] _retainedXAxes = [];

    public ISeries[] SendQueueSeries
    {
        get => _sendQueueSeries;
        set { _retainedSend = _sendQueueSeries; this.RaiseAndSetIfChanged(ref _sendQueueSeries, value); }
    }

    public ISeries[] RedoQueueSeries
    {
        get => _redoQueueSeries;
        set { _retainedRedo = _redoQueueSeries; this.RaiseAndSetIfChanged(ref _redoQueueSeries, value); }
    }

    public ISeries[] LagSeries
    {
        get => _lagSeries;
        set { _retainedLag = _lagSeries; this.RaiseAndSetIfChanged(ref _lagSeries, value); }
    }

    public ISeries[] LogBlockDiffSeries
    {
        get => _logBlockDiffSeries;
        set { _retainedLogDiff = _logBlockDiffSeries; this.RaiseAndSetIfChanged(ref _logBlockDiffSeries, value); }
    }

    // X-axis — rebuilt after each data load to show ~5 evenly-spaced time labels
    private Axis[] _xAxes = [CreateDefaultXAxis(TimeSpan.FromHours(24))];
    public Axis[] XAxes
    {
        get => _xAxes;
        set { _retainedXAxes = _xAxes; this.RaiseAndSetIfChanged(ref _xAxes, value); }
    }

    public Axis[] SendQueueYAxes { get; } = [CreateYAxis("KB")];
    public Axis[] RedoQueueYAxes { get; } = [CreateYAxis("KB")];
    public Axis[] LagYAxes { get; } = [CreateYAxis("Seconds")];
    public Axis[] LogBlockDiffYAxes { get; } = [CreateYAxis("Diff")];

    public ReactiveCommand<Unit, Unit> LoadCommand { get; }
    public ReactiveCommand<string, Unit> ExportCommand { get; }

    // Holds the loaded data for export
    private IReadOnlyList<SnapshotDataPoint> _loadedData = Array.Empty<SnapshotDataPoint>();

    public StatisticsViewModel(ISnapshotQueryService snapshotQuery, IConfigurationService configService)
    {
        _snapshotQuery = snapshotQuery;
        _configService = configService;
        LoadCommand = ReactiveCommand.CreateFromTask(LoadDataAsync);
        ExportCommand = ReactiveCommand.CreateFromTask<string>(ExportToExcelAsync);
    }

    public async Task InitializeAsync(StatisticsState? savedState = null)
    {
        _initializing = true;
        try
        {
            var filters = await _snapshotQuery.GetSnapshotFiltersAsync();

            GroupNames.Clear();
            GroupNames.Add("(All)");
            foreach (var g in filters.GroupNames) GroupNames.Add(g);

            ReplicaNames.Clear();
            ReplicaNames.Add("(All)");
            foreach (var r in filters.ReplicaNames) ReplicaNames.Add(r);

            DatabaseNames.Clear();
            DatabaseNames.Add("(All)");
            foreach (var d in filters.DatabaseNames) DatabaseNames.Add(d);

            // Restore saved filter selections (fall back to "(All)" if value no longer exists)
            if (savedState != null)
            {
                _selectedTimeRange = savedState.TimeRange != null && TimeRangeOptions.Contains(savedState.TimeRange)
                    ? savedState.TimeRange : "24 hours";
                _isCustomRange = _selectedTimeRange == "Custom";
                _selectedGroup = savedState.Group != null && GroupNames.Contains(savedState.Group)
                    ? savedState.Group : "(All)";
                _selectedReplica = savedState.Replica != null && ReplicaNames.Contains(savedState.Replica)
                    ? savedState.Replica : "(All)";
                _selectedDatabase = savedState.Database != null && DatabaseNames.Contains(savedState.Database)
                    ? savedState.Database : "(All)";

                this.RaisePropertyChanged(nameof(SelectedTimeRange));
                this.RaisePropertyChanged(nameof(IsCustomRange));
            }
            else
            {
                _selectedGroup = "(All)";
                _selectedReplica = "(All)";
                _selectedDatabase = "(All)";
            }

            this.RaisePropertyChanged(nameof(SelectedGroup));
            this.RaisePropertyChanged(nameof(SelectedReplica));
            this.RaisePropertyChanged(nameof(SelectedDatabase));
        }
        finally
        {
            _initializing = false;
        }

        await LoadDataAsync();

        // Enable auto-refresh after initial load if saved state requested it
        if (savedState?.AutoRefresh == true)
            AutoRefresh = true;
    }

    private async Task RefreshCascadingFiltersAsync(bool groupChanged)
    {
        var group = _selectedGroup == "(All)" ? null : _selectedGroup;
        var replica = _selectedReplica == "(All)" ? null : _selectedReplica;

        var filters = await _snapshotQuery.GetSnapshotFiltersAsync(group, groupChanged ? null : replica);

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
            "15 minutes" => (now.AddMinutes(-15), now),
            "1 hour" => (now.AddHours(-1), now),
            "4 hours" => (now.AddHours(-4), now),
            "8 hours" => (now.AddHours(-8), now),
            "24 hours" => (now.AddHours(-24), now),
            "7 days" => (now.AddDays(-7), now),
            "30 days" => (now.AddDays(-30), now),
            "90 days" => (now.AddDays(-90), now),
            "180 days" => (now.AddDays(-180), now),
            "365 days" => (now.AddDays(-365), now),
            "Custom" => (
                new DateTimeOffset(DateTime.SpecifyKind(CustomFrom ?? DateTime.Now.AddDays(-1), DateTimeKind.Local).ToUniversalTime(), TimeSpan.Zero),
                // CalendarDatePicker returns midnight; add one day for an exclusive upper bound
                // so selecting "March 31" includes all of March 31.
                new DateTimeOffset(DateTime.SpecifyKind((CustomUntil ?? DateTime.Now).AddDays(1), DateTimeKind.Local).ToUniversalTime(), TimeSpan.Zero)),
            _ => (now.AddHours(-24), now)
        };
    }

    public async Task LoadDataAsync(CancellationToken cancellationToken = default)
    {
        // Cancel any in-flight load so concurrent calls from multiple property
        // setters don't race against each other.
        _loadCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCts = cts;

        IsLoading = true;
        StatusText = "Loading...";

        try
        {
            var (since, until) = GetTimeRange();
            var group = _selectedGroup == "(All)" ? null : _selectedGroup;
            var replica = _selectedReplica == "(All)" ? null : _selectedReplica;
            var database = _selectedDatabase == "(All)" ? null : _selectedDatabase;

            var data = await _snapshotQuery.GetSnapshotDataAsync(since, until, group, replica, database, cts.Token);

            // If we were cancelled while awaiting, a newer load is in progress — discard results.
            if (cts.Token.IsCancellationRequested) return;

            _loadedData = data;

            if (data.Count > 0)
                _activeTier = data[0].Tier;

            // Rebuild X-axis with ~5 evenly-spaced labels for the queried range
            XAxes = [CreateDefaultXAxis(until - since)];

            // Build summaries and chart series off the UI thread (CPU-bound LINQ)
            var (summaryRows, send, redo, lag, logDiff) = await Task.Run(
                () =>
                {
                    var summary = BuildSummaryRows(data);
                    var charts = BuildChartSeries(data);
                    return (summary, charts.Send, charts.Redo, charts.Lag, charts.LogDiff);
                }, cts.Token).ConfigureAwait(true);

            if (cts.Token.IsCancellationRequested) return;

            SummaryRows = new ObservableCollection<SnapshotDataPoint>(summaryRows);

            SendQueueSeries = send;
            RedoQueueSeries = redo;
            LagSeries = lag;
            LogBlockDiffSeries = logDiff;

            var tierLabel = data.Count > 0 ? data[0].Tier.ToString().ToLowerInvariant() : "n/a";
            StatusText = $"{data.Count} data points loaded ({tierLabel} tier)";
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            // Superseded by a newer load — silently discard.
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            if (_loadCts == cts)
                IsLoading = false;
        }
    }

    /// <summary>
    /// Aggregates raw time-series data into one row per Group/Replica/Database,
    /// computing weighted averages for Avg columns and global Min/Max for extremes.
    /// </summary>
    private static List<SnapshotDataPoint> BuildSummaryRows(IReadOnlyList<SnapshotDataPoint> data)
    {
        if (data.Count == 0) return [];

        return data
            .GroupBy(d => (d.GroupName, d.ReplicaName, d.DatabaseName))
            .Select(g =>
            {
                var totalSamples = g.Sum(d => (long)d.SampleCount);
                // Most recent row for categorical/state fields
                var latest = g.MaxBy(d => d.Timestamp)!;

                return new SnapshotDataPoint
                {
                    Timestamp = latest.Timestamp,
                    GroupName = g.Key.GroupName,
                    ReplicaName = g.Key.ReplicaName,
                    DatabaseName = g.Key.DatabaseName,
                    SampleCount = (int)Math.Min(totalSamples, int.MaxValue),

                    LogSendQueueKbMin = g.Min(d => d.LogSendQueueKbMin),
                    LogSendQueueKbMax = g.Max(d => d.LogSendQueueKbMax),
                    LogSendQueueKbAvg = totalSamples > 0
                        ? g.Sum(d => d.LogSendQueueKbAvg * d.SampleCount) / totalSamples : 0,

                    RedoQueueKbMin = g.Min(d => d.RedoQueueKbMin),
                    RedoQueueKbMax = g.Max(d => d.RedoQueueKbMax),
                    RedoQueueKbAvg = totalSamples > 0
                        ? g.Sum(d => d.RedoQueueKbAvg * d.SampleCount) / totalSamples : 0,

                    LogSendRateMin = g.Min(d => d.LogSendRateMin),
                    LogSendRateMax = g.Max(d => d.LogSendRateMax),
                    LogSendRateAvg = totalSamples > 0
                        ? g.Sum(d => d.LogSendRateAvg * d.SampleCount) / totalSamples : 0,

                    RedoRateMin = g.Min(d => d.RedoRateMin),
                    RedoRateMax = g.Max(d => d.RedoRateMax),
                    RedoRateAvg = totalSamples > 0
                        ? g.Sum(d => d.RedoRateAvg * d.SampleCount) / totalSamples : 0,

                    LogBlockDiffMin = g.Min(d => d.LogBlockDiffMin),
                    LogBlockDiffMax = g.Max(d => d.LogBlockDiffMax),
                    LogBlockDiffAvg = totalSamples > 0
                        ? g.Sum(d => d.LogBlockDiffAvg * d.SampleCount) / totalSamples : 0,

                    SecondaryLagMin = g.Min(d => d.SecondaryLagMin),
                    SecondaryLagMax = g.Max(d => d.SecondaryLagMax),
                    SecondaryLagAvg = totalSamples > 0
                        ? g.Sum(d => d.SecondaryLagAvg * d.SampleCount) / totalSamples : 0,

                    Role = latest.Role,
                    SyncState = latest.SyncState,
                    AnySuspended = g.Any(d => d.AnySuspended),
                    LastHardenedLsn = latest.LastHardenedLsn,
                    LastCommitLsn = latest.LastCommitLsn,
                    Tier = latest.Tier
                };
            })
            .OrderBy(d => d.GroupName)
            .ThenBy(d => d.ReplicaName)
            .ThenBy(d => d.DatabaseName)
            .ToList();
    }

    private static (ISeries[] Send, ISeries[] Redo, ISeries[] Lag, ISeries[] LogDiff) BuildChartSeries(
        IReadOnlyList<SnapshotDataPoint> data)
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

        return (
            BuildLineSeries(groups, colors, d => d.LogSendQueueKbAvg),
            BuildLineSeries(groups, colors, d => d.RedoQueueKbAvg),
            BuildLineSeries(groups, colors, d => d.SecondaryLagAvg),
            BuildLineSeries(groups, colors, d => d.LogBlockDiffAvg));
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

    /// <summary>
    /// Creates a <see cref="DateTimeAxis"/> whose <see cref="Axis.MinStep"/> is set so that
    /// approximately 5 labels appear across the given time range.
    /// </summary>
    private static Axis CreateDefaultXAxis(TimeSpan range)
    {
        // Pick a human-friendly label format based on range width
        var format = range.TotalDays > 7
            ? "MM/dd"
            : range.TotalHours > 4
                ? "MM/dd HH:mm"
                : "HH:mm";

        // Step in hours — DateTimeAxis unit is TimeSpan.FromHours(1)
        var stepHours = Math.Max(range.TotalHours / 5.0, 1.0 / 60);

        return new DateTimeAxis(TimeSpan.FromHours(1),
            date => date.ToString(format, CultureInfo.InvariantCulture))
        {
            MinStep = stepHours,
            LabelsRotation = -45,
            TextSize = 11,
            LabelsPaint = new SolidColorPaint(SKColors.LightGray)
        };
    }

    private void ConfigureAutoRefresh(bool enabled)
    {
        _autoRefreshSubscription?.Dispose();
        _autoRefreshSubscription = null;

        if (!enabled || _autoRefreshPaused) return;

        var intervalSeconds = _configService.Load().GlobalPollingIntervalSeconds;

        _autoRefreshSubscription = Observable.Interval(TimeSpan.FromSeconds(intervalSeconds))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(_ => !_isLoading)
            .SelectMany(_ => Observable.FromAsync(LoadDataAsync))
            .Subscribe();
    }

    private bool _autoRefreshPaused;

    /// <summary>
    /// Temporarily suspends auto-refresh while the window is minimized
    /// to avoid SkiaSharp rendering on an invisible surface.
    /// </summary>
    public void PauseAutoRefresh()
    {
        _autoRefreshPaused = true;
        _autoRefreshSubscription?.Dispose();
        _autoRefreshSubscription = null;
    }

    /// <summary>
    /// Resumes auto-refresh when the window becomes visible again.
    /// </summary>
    public void ResumeAutoRefresh()
    {
        _autoRefreshPaused = false;
        if (_autoRefresh)
            ConfigureAutoRefresh(true);
    }

    public StatisticsState SaveState() => new()
    {
        TimeRange = _selectedTimeRange,
        Group = _selectedGroup,
        Replica = _selectedReplica,
        Database = _selectedDatabase,
        AutoRefresh = _autoRefresh
    };

    public void Dispose()
    {
        _autoRefreshSubscription?.Dispose();
        _autoRefreshSubscription = null;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        // Clear chart series and axes so the LiveCharts render loop stops
        // accessing SkiaSharp paint/font objects before they are finalized.
        SendQueueSeries = [];
        RedoQueueSeries = [];
        LagSeries = [];
        LogBlockDiffSeries = [];
        XAxes = [];

        // Release retained previous-generation series
        _retainedSend = [];
        _retainedRedo = [];
        _retainedLag = [];
        _retainedLogDiff = [];
        _retainedXAxes = [];
    }
}
