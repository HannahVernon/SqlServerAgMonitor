using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.ViewModels;

public class AddGroupViewModel : ViewModelBase
{
    private string _server = string.Empty;
    private string _authType = "windows";
    private string? _username;
    private string? _password;
    private bool _isDiscovering;
    private bool _isTestingConnection;
    private string? _statusMessage;
    private bool _connectionTested;
    private DiscoveredGroup? _selectedGroup;
    private int _pollingIntervalSeconds;
    private int _currentStep;

    public string Server
    {
        get => _server;
        set => this.RaiseAndSetIfChanged(ref _server, value);
    }

    public string AuthType
    {
        get => _authType;
        set => this.RaiseAndSetIfChanged(ref _authType, value);
    }

    public string? Username
    {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value);
    }

    public string? Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    public bool IsDiscovering
    {
        get => _isDiscovering;
        set => this.RaiseAndSetIfChanged(ref _isDiscovering, value);
    }

    public bool IsTestingConnection
    {
        get => _isTestingConnection;
        set => this.RaiseAndSetIfChanged(ref _isTestingConnection, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public bool ConnectionTested
    {
        get => _connectionTested;
        set => this.RaiseAndSetIfChanged(ref _connectionTested, value);
    }

    public DiscoveredGroup? SelectedGroup
    {
        get => _selectedGroup;
        set => this.RaiseAndSetIfChanged(ref _selectedGroup, value);
    }

    public int PollingIntervalSeconds
    {
        get => _pollingIntervalSeconds;
        set => this.RaiseAndSetIfChanged(ref _pollingIntervalSeconds, value);
    }

    public int CurrentStep
    {
        get => _currentStep;
        set => this.RaiseAndSetIfChanged(ref _currentStep, value);
    }

    public bool IsSqlAuth => string.Equals(AuthType, "sql", StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<DiscoveredGroup> DiscoveredGroups { get; } = new();

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> DiscoverCommand { get; }
    public ReactiveCommand<Unit, Unit> NextStepCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousStepCommand { get; }
    public ReactiveCommand<Unit, Unit> FinishCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public AddGroupViewModel()
    {
        PollingIntervalSeconds = 16;
        CurrentStep = 0;

        this.WhenAnyValue(x => x.AuthType)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsSqlAuth)));

        var canTest = this.WhenAnyValue(
            x => x.Server, x => x.IsTestingConnection,
            (server, testing) => !string.IsNullOrWhiteSpace(server) && !testing);

        var canDiscover = this.WhenAnyValue(x => x.ConnectionTested, x => x.IsDiscovering,
            (tested, discovering) => tested && !discovering);

        var canFinish = this.WhenAnyValue(x => x.SelectedGroup)
            .Select(group => group != null);

        TestConnectionCommand = ReactiveCommand.CreateFromTask(OnTestConnectionAsync, canTest);
        DiscoverCommand = ReactiveCommand.CreateFromTask(OnDiscoverAsync, canDiscover);
        NextStepCommand = ReactiveCommand.Create(OnNextStep);
        PreviousStepCommand = ReactiveCommand.Create(OnPreviousStep);
        FinishCommand = ReactiveCommand.Create(OnFinish, canFinish);
        CancelCommand = ReactiveCommand.Create(OnCancel);
    }

    private async Task OnTestConnectionAsync(CancellationToken cancellationToken)
    {
        IsTestingConnection = true;
        StatusMessage = $"Testing connection to {Server}...";

        try
        {
            // Will be wired to ISqlConnectionService.TestConnectionAsync by the host window
            await Task.Delay(500, cancellationToken);
            ConnectionTested = true;
            StatusMessage = "Connection successful.";
        }
        catch (Exception ex)
        {
            ConnectionTested = false;
            StatusMessage = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private async Task OnDiscoverAsync(CancellationToken cancellationToken)
    {
        IsDiscovering = true;
        StatusMessage = "Discovering AGs and DAGs...";
        DiscoveredGroups.Clear();

        try
        {
            // Will be wired to IAgDiscoveryService by the host window
            await Task.Delay(500, cancellationToken);
            StatusMessage = $"Found {DiscoveredGroups.Count} group(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    private void OnNextStep()
    {
        if (CurrentStep < 2)
            CurrentStep++;
    }

    private void OnPreviousStep()
    {
        if (CurrentStep > 0)
            CurrentStep--;
    }

    private void OnFinish()
    {
        // Will be handled by the host window to save config and start monitoring
    }

    private void OnCancel()
    {
        // Will close the window
    }
}
