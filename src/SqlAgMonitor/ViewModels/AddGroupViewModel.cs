using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private bool _credentialStored;
    private int _pollingIntervalSeconds;
    private int _currentStep;
    private bool _hasSelectedGroups;
    private bool _allDagMembersTested;

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

    public bool HasSelectedGroups
    {
        get => _hasSelectedGroups;
        set => this.RaiseAndSetIfChanged(ref _hasSelectedGroups, value);
    }

    public bool AllDagMembersTested
    {
        get => _allDagMembersTested;
        set => this.RaiseAndSetIfChanged(ref _allDagMembersTested, value);
    }

    /// <summary>Returns all groups the user has checked for monitoring.</summary>
    public IReadOnlyList<DiscoveredGroup> SelectedGroups =>
        DiscoveredGroups.Where(g => g.IsSelected).ToList();

    /// <summary>For backward compat — returns the first selected group or null.</summary>
    public DiscoveredGroup? SelectedGroup => SelectedGroups.FirstOrDefault();

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
            this.RaisePropertyChanged(nameof(IsStep3));
            this.RaisePropertyChanged(nameof(ShowNextButton));
            this.RaisePropertyChanged(nameof(ShowBackButton));
            this.RaisePropertyChanged(nameof(ShowFinishButton));
        }
    }

    /// <summary>True when any selected group is a distributed AG.</summary>
    public bool HasDagSelected => SelectedGroups.Any(g =>
        g.GroupType == AvailabilityGroupType.DistributedAvailabilityGroup);

    /// <summary>Maximum step index (3 if DAGs selected, else 2).</summary>
    private int MaxStep => HasDagSelected ? 3 : 2;

    public bool IsStep0 => CurrentStep == 0;
    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool ShowNextButton => CurrentStep < MaxStep;
    public bool ShowBackButton => CurrentStep > 0;
    public bool ShowFinishButton => CurrentStep == MaxStep;

    public bool IsSqlAuth => string.Equals(AuthType, "SQL Server", StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<DiscoveredGroup> DiscoveredGroups { get; } = new();

    /// <summary>DAG member connections for Step 3.</summary>
    public ObservableCollection<DagMemberConnectionVm> DagMemberConnections { get; } = new();

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> DiscoverCommand { get; }
    public ReactiveCommand<Unit, Unit> NextStepCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousStepCommand { get; }
    public ReactiveCommand<Unit, Unit> FinishCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> TestDagMembersCommand { get; }

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

        var canFinish = this.WhenAnyValue(
                x => x.HasSelectedGroups, x => x.AllDagMembersTested, x => x.CurrentStep,
                (hasGroups, dagOk, step) =>
                {
                    if (!hasGroups) return false;
                    if (step == 3) return dagOk;
                    return !HasDagSelected || dagOk;
                });

        TestConnectionCommand = ReactiveCommand.CreateFromTask(OnTestConnectionAsync, canTest);
        DiscoverCommand = ReactiveCommand.CreateFromTask(OnDiscoverAsync, canDiscover);
        NextStepCommand = ReactiveCommand.Create(OnNextStep);
        PreviousStepCommand = ReactiveCommand.Create(OnPreviousStep);
        FinishCommand = ReactiveCommand.Create(OnFinish, canFinish);
        CancelCommand = ReactiveCommand.Create(OnCancel);
        TestDagMembersCommand = ReactiveCommand.CreateFromTask(OnTestDagMembersAsync);
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
                _credentialStored = true;
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
            {
                group.IsSelected = true;
                group.WhenAnyValue(g => g.IsSelected)
                    .Subscribe(_ =>
                    {
                        HasSelectedGroups = DiscoveredGroups.Any(g => g.IsSelected);
                        this.RaisePropertyChanged(nameof(HasDagSelected));
                        this.RaisePropertyChanged(nameof(ShowNextButton));
                        this.RaisePropertyChanged(nameof(ShowFinishButton));
                    });
                DiscoveredGroups.Add(group);
            }

            HasSelectedGroups = DiscoveredGroups.Any(g => g.IsSelected);
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
        if (CurrentStep < MaxStep)
        {
            CurrentStep++;

            // When entering Step 3 (DAG members), populate member connections
            if (CurrentStep == 3)
                PopulateDagMemberConnections();
        }
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

    private async void OnCancel()
    {
        if (_credentialStored && _credentialStore is not null)
        {
            try { await _credentialStore.DeletePasswordAsync(CredentialKey); }
            catch { /* best-effort cleanup */ }
        }

        // Clean up any DAG member credentials
        foreach (var member in DagMemberConnections.Where(m => m.CredentialKey != null))
        {
            try { if (_credentialStore is not null) await _credentialStore.DeletePasswordAsync(member.CredentialKey!); }
            catch { /* best-effort cleanup */ }
        }

        CloseRequested?.Invoke(false);
    }

    /// <summary>
    /// Populates DagMemberConnections from selected DAGs' discovered members.
    /// Local member: pre-fill with the is_local=1 instance from discovery.
    /// Remote member: pre-fill with the AG listener name (user can edit).
    /// </summary>
    private void PopulateDagMemberConnections()
    {
        DagMemberConnections.Clear();
        AllDagMembersTested = false;

        foreach (var dag in SelectedGroups.Where(g =>
            g.GroupType == AvailabilityGroupType.DistributedAvailabilityGroup))
        {
            foreach (var member in dag.DagMembers)
            {
                string defaultServer;
                if (member.IsLocal)
                {
                    // Use the discovered is_local=1 instance, or fall back to Step 0 server
                    var localInstance = member.Instances.FirstOrDefault(i => i.IsLocal);
                    defaultServer = localInstance?.ServerName ?? Server;
                }
                else
                {
                    // Use the AG listener name for remote members (user can edit)
                    defaultServer = member.MemberAgName;
                }

                var memberVm = new DagMemberConnectionVm
                {
                    DagName = dag.Name,
                    MemberAgName = member.MemberAgName,
                    DagRoleDesc = member.DagRoleDesc,
                    IsLocal = member.IsLocal,
                    AuthType = ResolvedAuthType,
                    Server = defaultServer
                };

                // For SQL auth, pre-fill username from Step 0
                if (IsSqlAuth)
                {
                    memberVm.Username = Username;
                }

                // If local member and already tested with initial connection, mark as tested
                if (member.IsLocal && ConnectionTested)
                {
                    memberVm.ConnectionTested = true;
                    memberVm.ConnectionSucceeded = true;
                    memberVm.StatusMessage = "Connected (initial connection)";
                    if (IsSqlAuth)
                    {
                        memberVm.CredentialKey = CredentialKey;
                    }
                }

                DagMemberConnections.Add(memberVm);
            }
        }

        UpdateAllDagMembersTested();
    }

    private async Task OnTestDagMembersAsync(CancellationToken cancellationToken)
    {
        if (_connectionService is null) return;

        var untested = DagMemberConnections
            .Where(m => !m.ConnectionSucceeded && !m.IsTesting)
            .ToList();

        foreach (var member in untested)
        {
            member.IsTesting = true;
            member.StatusMessage = $"Testing {member.Server}...";

            try
            {
                // For SQL auth, store the member's credentials
                if (member.IsSqlAuth && _credentialStore is not null && !string.IsNullOrEmpty(member.Password))
                {
                    var memberCredKey = $"agmon:{member.Server}:{member.Username}";
                    await _credentialStore.StorePasswordAsync(memberCredKey, member.Password, cancellationToken);
                    member.CredentialKey = memberCredKey;
                }
                else if (!member.IsSqlAuth)
                {
                    member.CredentialKey = null;
                }

                var success = await _connectionService.TestConnectionAsync(
                    member.Server,
                    member.IsSqlAuth ? member.Username : null,
                    member.CredentialKey,
                    member.AuthType,
                    cancellationToken);

                member.ConnectionTested = true;
                member.ConnectionSucceeded = success;
                member.StatusMessage = success ? "Connected" : "Connection failed";
            }
            catch (Exception ex)
            {
                member.ConnectionTested = true;
                member.ConnectionSucceeded = false;
                member.StatusMessage = $"Failed: {ex.Message}";
            }
            finally
            {
                member.IsTesting = false;
            }
        }

        UpdateAllDagMembersTested();
    }

    private void UpdateAllDagMembersTested()
    {
        AllDagMembersTested = DagMemberConnections.Count > 0 &&
                              DagMemberConnections.All(m => m.ConnectionSucceeded);
    }
}
