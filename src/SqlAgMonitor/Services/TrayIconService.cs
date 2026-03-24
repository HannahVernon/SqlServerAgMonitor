using System;
using Avalonia.Controls;

namespace SqlAgMonitor.Services;

public class TrayIconService : IDisposable
{
    private TrayIcon? _trayIcon;
    private bool _disposed;

    public event EventHandler? ShowWindowRequested;
    public event EventHandler? PauseAllRequested;
    public event EventHandler? ResumeAllRequested;
    public event EventHandler? QuitRequested;

    public void Initialize()
    {
        _trayIcon = new TrayIcon
        {
            ToolTipText = "SQL Server AG Monitor",
            IsVisible = true,
            Menu = CreateMenu()
        };

        _trayIcon.Clicked += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private NativeMenu CreateMenu()
    {
        var menu = new NativeMenu();

        var openItem = new NativeMenuItem("Open Monitor");
        openItem.Click += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        menu.Add(openItem);

        menu.Add(new NativeMenuItemSeparator());

        var pauseItem = new NativeMenuItem("Pause All Monitoring");
        pauseItem.Click += (_, _) => PauseAllRequested?.Invoke(this, EventArgs.Empty);
        menu.Add(pauseItem);

        var resumeItem = new NativeMenuItem("Resume All Monitoring");
        resumeItem.Click += (_, _) => ResumeAllRequested?.Invoke(this, EventArgs.Empty);
        menu.Add(resumeItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        menu.Add(quitItem);

        return menu;
    }

    public void UpdateToolTip(string text)
    {
        if (_trayIcon != null)
            _trayIcon.ToolTipText = text;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _trayIcon?.Dispose();
            _disposed = true;
        }
    }
}
