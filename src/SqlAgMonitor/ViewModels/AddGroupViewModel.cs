using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Connection;
using SqlAgMonitor.Core.Services.Credentials;
using SqlAgMonitor.Core.Services.Monitoring;

namespace SqlAgMonitor.ViewModels;

public class AddGroupViewModel : ViewModelBase
{
    private readonly ISqlConnectionService? _connectionService;
    private readonly IAgDiscoveryService? _discoveryService;
    private readonly ICredentialStore? _credentialStore;

    private string _server = string.Empty;
    private string _authType = "Windows";
    private string? _username;
    private string? _password;
    private bool _isDiscovering;
    private bool _isTestingConnection;
    private string? _statusMessage;
    private bool _connectionTested;
    private DiscoveredGroup? _selectedGroup;
    private int _pollingIntervalSeconds;
    private int _currentStep;

    public List<string> AuthTypeOptions { get; } = new() { "Windows", "SQL Server" };

    public string Server
    {
        get => _server;
        set => this.RaiseAndSetIfChanged(ref _server, value);
    }

    public string AuthType
    {
        get => _authType;
        set
        {
            this.RaiseAndSetIfChanged(ref _authType, value);
            this.RaisePropertyChanged(nameof(IsSqlAuth));
        }
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
        set
        {
            this.RaiseAndSetIfChanged(ref _currentStep, value);
            this.RaisePropertyChanged(nameof(IsStep0));
            this.RaisePropertyChanged(nameof(IsStep1));
            this.RaisePropertyChanged(nameof(IsStep2));
            this.RaisePropertyChanged(nameof(ShowNextButton));
            this.RaisePropertyChanged(nameof(ShowBackButton));
            this.RaisePropertyChanged(nameof(ShowFinishButton));
        }
    }

    public bool IsStep0 => CurrentStep == 0;
    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool ShowNextButton => CurrentStep < 2;
    public bool ShowBackButton => CurrentStep > 0;
    public bool ShowFinishButton => CurrentStep == 2;

    public bool IsSqlAuth => string.Equals(AuthType, "SQL Server", StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<DiscoveredGroup> DiscoveredGroups { get; } = new();

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> DiscoverCommand { get; }
    public ReactiveCommand<Unit, Unit> NextStepCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousStepCommand { get; }
    public ReactiveCommand<Unit, Unit> FinishCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Raised when the dialog should close. True = finished (add group), False = cancelled.</summary>
    public event Action<bool>? CloseRequested;

    public AddGroupViewModel()
        : this(null, null, null)
    {
    }

    public AddGroupViewModel(
        ISqlConnectionService? connectionService,
        IAgDiscoveryService? discoveryService,
        ICredentialStore? credentialStore)
    {
        _connectionService = connectionService;
        _discoveryService = discoveryService;
        _credentialStore = credentialStore;

        PollingIntervalSeconds = 16;
        CurrentStep = 0;

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

    private string CredentialKey => $"agmon:{Server}:{Username}";

    private string ResolvedAuthType =>
        string.Equals(AuthType, "SQL Server", StringComparison.OrdinalIgnoreCase) ? "sql" : "windows";

    private async Task OnTestConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connectionService is null)
        {
            StatusMessage = "Connection service not available.";
            return;
        }

        IsTestingConnection = true;
        StatusMessage = $"Testing connection to {Server}...";

        try
        {
            if (IsSqlAuth && _credentialStore is not null && !string.IsNullOrEmpty(Password))
            {
                await _credentialStore.StorePasswordAsync(CredentialKey, Password, cancellationToken);
            }

            var success = await _connectionService.TestConnectionAsync(
                Server, Username, IsSqlAuth ? CredentialKey : null, ResolvedAuthType, cancellationToken);

            ConnectionTested = success;
            StatusMessage = success ? "Connection successful." : "Connection failed. Check server name and credentials.";
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
        if (_discoveryService is null)
        {
            StatusMessage = "Discovery service not available.";
            return;
        }

        IsDiscovering = true;
        StatusMessage = "Discovering AGs and DAGs...";
        DiscoveredGroups.Clear();

        try
        {
            var groups = await _discoveryService.DiscoverGroupsAsync(
                Server, Username, IsSqlAuth ? CredentialKey : null, ResolvedAuthType, cancellationToken);

            foreach (var group in groups)
                DiscoveredGroups.Add(group);

            StatusMessage = $"Found {DiscoveredGroups.Count} group(s).";

            if (DiscoveredGroups.Count == 1)
                SelectedGroup = DiscoveredGroups[0];
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
        CloseRequested?.Invoke(true);
    }

    private void OnCancel()
    {
        CloseRequested?.Invoke(false);
    }
}
