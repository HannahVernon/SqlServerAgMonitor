using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SqlAgMonitor.Views;

public partial class MigrationDialog : Window
{
    private readonly List<string> _groupNames;
    private readonly Func<Task<string>>? _migrateAction;

    public bool Migrated { get; private set; }

    public MigrationDialog()
    {
        InitializeComponent();
        _groupNames = new List<string>();
    }

    public MigrationDialog(List<string> groupNames, List<string> sqlAuthGroupNames, Func<Task<string>> migrateAction)
    {
        InitializeComponent();
        _groupNames = groupNames;
        _migrateAction = migrateAction;

        var heading = this.FindControl<TextBlock>("HeadingText")!;
        var description = this.FindControl<TextBlock>("DescriptionText")!;
        var groupList = this.FindControl<TextBlock>("GroupListText")!;
        var warning = this.FindControl<TextBlock>("WarningText")!;

        heading.Text = "Push local configuration to the service?";
        description.Text = $"You've enabled service mode. The following {groupNames.Count} monitored group(s) and settings can be migrated to the remote service:";
        groupList.Text = string.Join("\n", groupNames.Select(n => $"  • {n}"));

        if (sqlAuthGroupNames.Count > 0)
        {
            warning.Text = $"⚠ Groups using SQL Server authentication ({string.Join(", ", sqlAuthGroupNames)}) will need their passwords re-entered on the service side — passwords cannot be transferred.";
        }
        else
        {
            warning.IsVisible = false;
        }

        var migrateBtn = this.FindControl<Button>("MigrateBtn")!;
        var skipBtn = this.FindControl<Button>("SkipBtn")!;
        var closeBtn = this.FindControl<Button>("CloseBtn")!;

        migrateBtn.Click += OnMigrateAsync;
        skipBtn.Click += OnSkip;
        closeBtn.Click += OnClose;
    }

    private async void OnMigrateAsync(object? sender, RoutedEventArgs e)
    {
        if (_migrateAction == null) return;

        var migrateBtn = this.FindControl<Button>("MigrateBtn")!;
        var skipBtn = this.FindControl<Button>("SkipBtn")!;
        var buttonPanel = this.FindControl<StackPanel>("ButtonPanel")!;
        var resultText = this.FindControl<TextBlock>("ResultText")!;
        var closeBtn = this.FindControl<Button>("CloseBtn")!;

        migrateBtn.IsEnabled = false;
        migrateBtn.Content = "Migrating…";

        try
        {
            var result = await _migrateAction();
            Migrated = true;
            resultText.Text = result;
            resultText.IsVisible = true;
        }
        catch (Exception ex)
        {
            resultText.Text = $"✗ Migration failed: {ex.Message}";
            resultText.IsVisible = true;
        }

        buttonPanel.IsVisible = false;
        closeBtn.IsVisible = true;
    }

    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        Migrated = false;
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
