using NSubstitute;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Connection;
using SqlAgMonitor.Core.Services.Credentials;
using SqlAgMonitor.Core.Services.Monitoring;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Tests.ViewModels;

public sealed class AddGroupViewModelTests : IDisposable
{
    private readonly ISqlConnectionService _connectionService;
    private readonly IAgDiscoveryService _discoveryService;
    private readonly ICredentialStore _credentialStore;
    private readonly AddGroupViewModel _vm;

    public AddGroupViewModelTests()
    {
        _connectionService = Substitute.For<ISqlConnectionService>();
        _discoveryService = Substitute.For<IAgDiscoveryService>();
        _credentialStore = Substitute.For<ICredentialStore>();
        _vm = new AddGroupViewModel(_connectionService, _discoveryService, _credentialStore);
    }

    public void Dispose() => _vm.Dispose();

    [Fact]
    public void InitialStep_IsZero()
    {
        Assert.Equal(0, _vm.CurrentStep);
    }

    [Fact]
    public void ServerName_RaisesPropertyChanged()
    {
        var raised = false;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AddGroupViewModel.Server))
                raised = true;
        };

        _vm.Server = "SQL01";

        Assert.True(raised);
        Assert.Equal("SQL01", _vm.Server);
    }

    [Fact]
    public void AuthType_DefaultsToWindows()
    {
        Assert.Equal("Windows", _vm.AuthType);
    }

    [Fact]
    public void IsSqlAuth_FalseByDefault()
    {
        Assert.False(_vm.IsSqlAuth);
    }

    [Fact]
    public void PollingIntervalSeconds_DefaultIs16()
    {
        Assert.Equal(16, _vm.PollingIntervalSeconds);
    }

    [Fact]
    public void Cancel_InvokesCloseRequested_WithFalse()
    {
        bool? closeValue = null;
        _vm.CloseRequested += value => closeValue = value;

        _vm.CancelCommand.Execute().Subscribe();

        Assert.NotNull(closeValue);
        Assert.False(closeValue);
    }

    [Fact]
    public void Finish_InvokesCloseRequested_WithTrue()
    {
        // Finish requires HasSelectedGroups=true to be enabled.
        // Set up the ViewModel state so the FinishCommand can execute.
        _vm.HasSelectedGroups = true;
        _vm.AllDagMembersTested = true;

        bool? closeValue = null;
        _vm.CloseRequested += value => closeValue = value;

        _vm.FinishCommand.Execute().Subscribe();

        Assert.NotNull(closeValue);
        Assert.True(closeValue);
    }

    [Fact]
    public void NextStep_AdvancesStep_WhenGated()
    {
        // Step 0 → 1 requires ConnectionTested=true AND HasDiscoveredGroups=true
        _vm.ConnectionTested = true;
        _vm.HasDiscoveredGroups = true;

        _vm.NextStepCommand.Execute().Subscribe();

        Assert.Equal(1, _vm.CurrentStep);
    }

    [Fact]
    public void PreviousStep_DecreasesStep()
    {
        // Advance to step 1 first
        _vm.ConnectionTested = true;
        _vm.HasDiscoveredGroups = true;
        _vm.NextStepCommand.Execute().Subscribe();
        Assert.Equal(1, _vm.CurrentStep);

        _vm.PreviousStepCommand.Execute().Subscribe();

        Assert.Equal(0, _vm.CurrentStep);
    }

    [Fact]
    public void PreviousStep_AtZero_StaysAtZero()
    {
        _vm.PreviousStepCommand.Execute().Subscribe();

        Assert.Equal(0, _vm.CurrentStep);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var vm = new AddGroupViewModel(_connectionService, _discoveryService, _credentialStore);
        var ex = Record.Exception(() => vm.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void DiscoveredGroups_InitiallyEmpty()
    {
        Assert.Empty(_vm.DiscoveredGroups);
    }
}
