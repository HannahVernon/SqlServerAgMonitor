using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace SqlAgMonitor.Views;

public partial class MigrationDialog : Window
{
    private readonly Func<List<string>, Task<string>>? _migrateAction;
    private readonly List<CheckBox> _groupCheckboxes = new();

    public bool Migrated { get; private set; }

    public MigrationDialog()
    {
        InitializeComponent();
    }

    public MigrationDialog(
        List<string> localGroupNames,
        List<string> serviceGroupNames,
        List<string> sqlAuthGroupNames,
        Func<List<string>, Task<string>> migrateAction)
    {
        InitializeComponent();
        _migrateAction = migrateAction;

        var heading = this.FindControl<TextBlock>("HeadingText")!;
        var description = this.FindControl<TextBlock>("DescriptionText")!;
        var warning = this.FindControl<TextBlock>("WarningText")!;
        var groupPanel = this.FindControl<StackPanel>("GroupPanel")!;

        heading.Text = "Migrate local groups to the service";
        description.Text = "Select which local server groups to push to the remote service. Alert, email, and syslog settings are always included.";

        var localOnly = localGroupNames
            .Where(n => !serviceGroupNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var shared = localGroupNames
            .Where(n => serviceGroupNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var serviceOnly = serviceGroupNames
            .Where(n => !localGroupNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (localOnly.Count > 0)
        {
            AddSection(groupPanel, "New — local only (not on service)", localOnly, isChecked: true);
        }

        if (shared.Count > 0)
        {
            AddSection(groupPanel, "Shared — exists on both (will overwrite service config)", shared, isChecked: false);
        }

        if (serviceOnly.Count > 0)
        {
            var svcHeader = new TextBlock
            {
                Text = "Service only — already on the service (no action needed)",
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 4, 0, 4)
            };
            groupPanel.Children.Add(svcHeader);

            foreach (var name in serviceOnly)
            {
                var label = new TextBlock
                {
                    Text = $"  ✓ {name}",
                    Opacity = 0.5,
                    Margin = new Avalonia.Thickness(8, 2, 0, 2)
                };
                groupPanel.Children.Add(label);
            }
        }

        if (localOnly.Count == 0 && shared.Count == 0)
        {
            description.Text = "The service already has all your local groups. Nothing to migrate.";
            this.FindControl<Button>("MigrateBtn")!.IsEnabled = false;
        }

        if (sqlAuthGroupNames.Count > 0)
        {
            var relevantSqlAuth = sqlAuthGroupNames
                .Where(n => localGroupNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (relevantSqlAuth.Count > 0)
            {
                warning.Text = $"⚠ Groups using SQL Server authentication ({string.Join(", ", relevantSqlAuth)}) will need their passwords re-entered on the service side — passwords cannot be transferred.";
            }
            else
            {
                warning.IsVisible = false;
            }
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

    private void AddSection(StackPanel parent, string label, List<string> groupNames, bool isChecked)
    {
        var header = new TextBlock
        {
            Text = label,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Margin = new Avalonia.Thickness(0, 4, 0, 4)
        };
        parent.Children.Add(header);

        foreach (var name in groupNames)
        {
            var cb = new CheckBox
            {
                Content = name,
                IsChecked = isChecked,
                Margin = new Avalonia.Thickness(8, 2, 0, 2),
                Tag = name
            };
            _groupCheckboxes.Add(cb);
            parent.Children.Add(cb);
        }
    }

    private List<string> GetSelectedGroups()
    {
        return _groupCheckboxes
            .Where(cb => cb.IsChecked == true)
            .Select(cb => cb.Tag as string ?? "")
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
    }

    private async void OnMigrateAsync(object? sender, RoutedEventArgs e)
    {
        if (_migrateAction == null) return;

        var selected = GetSelectedGroups();
        if (selected.Count == 0)
        {
            var resultText = this.FindControl<TextBlock>("ResultText")!;
            resultText.Text = "No groups selected.";
            resultText.IsVisible = true;
            return;
        }

        var migrateBtn = this.FindControl<Button>("MigrateBtn")!;
        var buttonPanel = this.FindControl<StackPanel>("ButtonPanel")!;
        var resultBlock = this.FindControl<TextBlock>("ResultText")!;
        var closeBtn = this.FindControl<Button>("CloseBtn")!;

        migrateBtn.IsEnabled = false;
        migrateBtn.Content = "Migrating…";

        try
        {
            var result = await _migrateAction(selected);
            Migrated = true;
            resultBlock.Text = result;
            resultBlock.IsVisible = true;
        }
        catch (Exception ex)
        {
            resultBlock.Text = $"✗ Migration failed: {ex.Message}";
            resultBlock.IsVisible = true;
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
