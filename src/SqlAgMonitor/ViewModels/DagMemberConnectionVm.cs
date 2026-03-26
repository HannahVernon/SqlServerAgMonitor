using System;
using ReactiveUI;

namespace SqlAgMonitor.ViewModels;

/// <summary>
/// ViewModel for a single DAG member connection in the wizard's Step 3.
/// Tracks the connection endpoint, credentials, and test status.
/// </summary>
public class DagMemberConnectionVm : ReactiveObject
{
    private string _server = string.Empty;
    private string? _username;
    private string? _password;
    private bool _isTesting;
    private bool _connectionTested;
    private bool _connectionSucceeded;
    private string? _statusMessage;

    /// <summary>Name of the distributed AG this member belongs to.</summary>
    public string DagName { get; init; } = string.Empty;

    /// <summary>AG listener name for this member.</summary>
    public string MemberAgName { get; init; } = string.Empty;

    /// <summary>PRIMARY or SECONDARY at the DAG level.</summary>
    public string? DagRoleDesc { get; init; }

    /// <summary>Whether this member's AG was detected on the initially-connected server.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Auth type: "windows" or "sql".</summary>
    public string AuthType { get; set; } = "windows";

    public string Server
    {
        get => _server;
        set => this.RaiseAndSetIfChanged(ref _server, value);
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

    public bool IsTesting
    {
        get => _isTesting;
        set => this.RaiseAndSetIfChanged(ref _isTesting, value);
    }

    public bool ConnectionTested
    {
        get => _connectionTested;
        set => this.RaiseAndSetIfChanged(ref _connectionTested, value);
    }

    public bool ConnectionSucceeded
    {
        get => _connectionSucceeded;
        set => this.RaiseAndSetIfChanged(ref _connectionSucceeded, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    /// <summary>Credential store key (set after password is stored).</summary>
    public string? CredentialKey { get; set; }

    public bool IsSqlAuth => string.Equals(AuthType, "sql", StringComparison.OrdinalIgnoreCase);
}
