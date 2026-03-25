using System;
using System.Collections.ObjectModel;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SqlAgMonitor.Views;

public partial class NotificationOverlay : UserControl
{
    private readonly Timer _cleanupTimer;

    public ObservableCollection<ToastNotification> ActiveNotifications { get; } = new();

    public NotificationOverlay()
    {
        InitializeComponent();

        NotificationList.ItemsSource = ActiveNotifications;

        _cleanupTimer = new Timer(1000);
        _cleanupTimer.Elapsed += OnCleanupTimerElapsed;
        _cleanupTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cleanupTimer.Stop();
        _cleanupTimer.Elapsed -= OnCleanupTimerElapsed;
        _cleanupTimer.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    public void ShowNotification(string title, string message, TimeSpan? duration = null)
    {
        var notification = new ToastNotification
        {
            Title = title,
            Message = message,
            Timestamp = DateTimeOffset.Now.ToString("HH:mm:ss"),
            ExpiresAt = DateTimeOffset.Now.Add(duration ?? TimeSpan.FromSeconds(8))
        };

        Dispatcher.UIThread.Post(() =>
        {
            ActiveNotifications.Add(notification);

            while (ActiveNotifications.Count > 5)
                ActiveNotifications.RemoveAt(0);
        });
    }

    private void OnCleanupTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var now = DateTimeOffset.Now;
            for (int i = ActiveNotifications.Count - 1; i >= 0; i--)
            {
                if (ActiveNotifications[i].ExpiresAt <= now)
                    ActiveNotifications.RemoveAt(i);
            }
        });
    }
}

public class ToastNotification
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}
