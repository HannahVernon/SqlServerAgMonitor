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

    /// <summary>Per-tab grid column layouts, keyed by tab title (AG/DAG name).</summary>
    public Dictionary<string, TabGridLayout> TabLayouts { get; set; } = new();

    [Obsolete("Use TabLayouts instead. Kept for migration from old layout files.")]
    public Dictionary<string, double>? ColumnWidths { get; set; }
    [Obsolete("Use TabLayouts instead. Kept for migration from old layout files.")]
    public Dictionary<string, int>? ColumnDisplayIndices { get; set; }
}

public static class LayoutStateService
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

    public static WindowLayoutState Load()
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

    public static void Save(WindowLayoutState state)
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
