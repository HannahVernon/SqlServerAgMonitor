using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlAgMonitor.Services;

public class WindowLayoutState
{
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool IsMaximized { get; set; }
    public double SplitterTopProportion { get; set; } = 0.5;
    public Dictionary<string, double> ColumnWidths { get; set; } = new();
    public Dictionary<string, int> ColumnDisplayIndices { get; set; } = new();
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
