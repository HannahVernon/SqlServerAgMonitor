using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlAgMonitor.Services;

public class TabGridLayout
{
    public Dictionary<string, double> ColumnWidths { get; set; } = new();
    public Dictionary<string, int> ColumnDisplayIndices { get; set; } = new();
}

public class StatisticsState
{
    public string? TimeRange { get; set; }
    public string? Group { get; set; }
    public string? Replica { get; set; }
    public string? Database { get; set; }
    public bool AutoRefresh { get; set; }
}

public class WindowLayoutState
{
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool IsMaximized { get; set; }
    public double SplitterTopProportion { get; set; } = 0.5;

    // Statistics window
    public double? StatsWindowX { get; set; }
    public double? StatsWindowY { get; set; }
    public double? StatsWindowWidth { get; set; }
    public double? StatsWindowHeight { get; set; }
    public StatisticsState? StatsState { get; set; }

    /// <summary>Column widths for the statistics summary DataGrid.</summary>
    public TabGridLayout? StatsGridLayout { get; set; }

    /// <summary>Per-tab grid column layouts, keyed by tab title (AG/DAG name).</summary>
    public Dictionary<string, TabGridLayout> TabLayouts { get; set; } = new();
}

public class LayoutStateService : ILayoutStateService
{
    private static readonly string LayoutFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SqlAgMonitor", "layout.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WindowLayoutState Load()
    {
        try
        {
            if (File.Exists(LayoutFilePath))
            {
                var json = File.ReadAllText(LayoutFilePath);
                return JsonSerializer.Deserialize<WindowLayoutState>(json, JsonOptions)
                       ?? new WindowLayoutState();
            }
        }
        catch
        {
            // Corrupt layout file — use defaults
        }

        return new WindowLayoutState();
    }

    public void Save(WindowLayoutState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(LayoutFilePath);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(LayoutFilePath, json);
        }
        catch
        {
            // Layout persistence is best-effort
        }
    }
}
