using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.History;

namespace SqlAgMonitor.ViewModels;

public class AlertHistoryViewModel : ViewModelBase
{
    private bool _isLoading;
    private string _statusText = string.Empty;

    public ObservableCollection<AlertEvent> Events { get; } = new();

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

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public AlertHistoryViewModel()
    {
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadEventsAsync);
    }

    public async Task LoadEventsAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusText = "Loading...";
        try
        {
            var historyService = App.Services?.GetService<IEventHistoryService>();
            if (historyService == null)
            {
                StatusText = "History service unavailable.";
                return;
            }

            var events = await historyService.GetEventsAsync(limit: 1000, cancellationToken: cancellationToken);
            var count = await historyService.GetEventCountAsync(cancellationToken: cancellationToken);

            Events.Clear();
            foreach (var evt in events)
                Events.Add(evt);

            StatusText = $"{Events.Count} of {count} events shown";
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
}
