using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Services;

/// <summary>
/// SignalR-based monitoring client that replaces <see cref="MonitoringCoordinator"/>
/// when the app is configured in service-client mode. Receives snapshot and alert
/// push events from the remote SqlAgMonitor Windows Service via SignalR.
/// </summary>
public sealed class ServiceMonitoringClient : IMonitoringCoordinator
{
    private readonly IConfigurationService _configService;
    private readonly ILogger<ServiceMonitoringClient> _logger;
    private readonly Dictionary<string, MonitoredGroupSnapshot> _latestSnapshots = new(StringComparer.OrdinalIgnoreCase);

    private HubConnection? _hubConnection;
    private string? _trustedCertThumbprint;
    private readonly CancellationTokenSource _reconnectCts = new();

    public ObservableCollection<MonitorTabViewModel> MonitorTabs { get; } = new();

    /// <summary>
    /// Raised on the UI thread after a snapshot is received from the service.
    /// </summary>
    public event Action<MonitoredGroupSnapshot>? SnapshotProcessed;

    /// <summary>
    /// Raised on the UI thread when an alert is received from the service.
    /// </summary>
    public event Action<AlertEvent>? AlertRaised;

    /// <summary>
    /// Raised when the SignalR connection state changes. True = connected.
    /// </summary>
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>
    /// Raised on the UI thread when a reconnect detects an incompatible service version.
    /// The string contains the error message to display.
    /// </summary>
    public event Action<string>? VersionMismatchDetected;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Signals that an external connection attempt failed, updating UI state.
    /// </summary>
    public void NotifyConnectionFailed()
    {
        Dispatcher.UIThread.Post(() => ConnectionStateChanged?.Invoke(false));
    }

    public ServiceMonitoringClient(
        IConfigurationService configService,
        ILogger<ServiceMonitoringClient> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Connects to the remote service and begins receiving push events.
    /// Call once at startup when service-client mode is enabled.
    /// </summary>
    public async Task ConnectAsync(string jwtToken, string? trustedCertThumbprint = null, CancellationToken cancellationToken = default)
    {
        _trustedCertThumbprint = trustedCertThumbprint;
        var config = _configService.Load();
        var serviceConfig = config.Service;
        var scheme = serviceConfig.UseTls ? "https" : "http";
        var hubUrl = $"{scheme}://{serviceConfig.Host}:{Math.Clamp(serviceConfig.Port, 1, 65535)}/monitor";

        _logger.LogInformation("Connecting to service hub at {Url}", hubUrl);

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(jwtToken);

                if (trustedCertThumbprint is not null && serviceConfig.UseTls)
                {
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler clientHandler)
                        {
                            clientHandler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                                errors == SslPolicyErrors.None ||
                                (cert != null && string.Equals(cert.GetCertHashString(), trustedCertThumbprint, StringComparison.OrdinalIgnoreCase));
                        }
                        return handler;
                    };
                }
            })
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        WireHubCallbacks(_hubConnection);

        _hubConnection.Reconnecting += _ =>
        {
            _logger.LogWarning("SignalR connection lost, reconnecting...");
            Dispatcher.UIThread.Post(() => ConnectionStateChanged?.Invoke(false));
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += async connectionId =>
        {
            _logger.LogInformation("SignalR reconnected (connection {Id}), checking service version", connectionId);

            try
            {
                var currentConfig = _configService.Load();
                var versionError = await CheckVersionAsync(currentConfig.Service, _trustedCertThumbprint);
                if (versionError != null)
                {
                    _logger.LogError("Service version incompatible after reconnect: {Error}", versionError);
                    Dispatcher.UIThread.Post(() =>
                    {
                        ConnectionStateChanged?.Invoke(false);
                        VersionMismatchDetected?.Invoke(versionError);
                    });
                    await _hubConnection.StopAsync();
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Version check failed on reconnect, continuing anyway");
            }

            Dispatcher.UIThread.Post(() => ConnectionStateChanged?.Invoke(true));
            _ = LoadCurrentSnapshotsAsync();
        };

        _hubConnection.Closed += ex =>
        {
            if (ex is not null)
                _logger.LogError(ex, "SignalR connection closed with error");
            Dispatcher.UIThread.Post(() => ConnectionStateChanged?.Invoke(false));
            return Task.CompletedTask;
        };

        await _hubConnection.StartAsync(cancellationToken);
        _logger.LogInformation("Connected to service hub");
        ConnectionStateChanged?.Invoke(true);

        // Load initial state
        await LoadCurrentSnapshotsAsync();
    }

    /// <summary>
    /// Authenticates with the service and returns a JWT token.
    /// </summary>
    public static async Task<string?> LoginAsync(
        ServiceSettings serviceConfig, string username, string password,
        string? trustedCertThumbprint = null,
        CancellationToken cancellationToken = default)
    {
        var scheme = serviceConfig.UseTls ? "https" : "http";
        var baseUrl = $"{scheme}://{serviceConfig.Host}:{Math.Clamp(serviceConfig.Port, 1, 65535)}";

        HttpClientHandler? handler = null;
        if (trustedCertThumbprint is not null && serviceConfig.UseTls)
        {
            handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                    errors == SslPolicyErrors.None ||
                    (cert != null && string.Equals(cert.GetCertHashString(), trustedCertThumbprint, StringComparison.OrdinalIgnoreCase))
            };
        }

        using var client = handler is not null
            ? new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri(baseUrl) }
            : new HttpClient { BaseAddress = new Uri(baseUrl) };
        var payload = new { username, password };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/auth/login", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
        return doc.RootElement.TryGetProperty("token", out var tokenProp)
            ? tokenProp.GetString()
            : null;
    }

    /// <summary>
    /// Checks the remote service protocol version. Returns null if compatible,
    /// or an error message string if the service is incompatible or unreachable.
    /// </summary>
    public static async Task<string?> CheckVersionAsync(
        ServiceSettings serviceConfig,
        string? trustedCertThumbprint = null,
        CancellationToken cancellationToken = default)
    {
        var scheme = serviceConfig.UseTls ? "https" : "http";
        var baseUrl = $"{scheme}://{serviceConfig.Host}:{Math.Clamp(serviceConfig.Port, 1, 65535)}";

        HttpClientHandler? handler = null;
        if (trustedCertThumbprint is not null && serviceConfig.UseTls)
        {
            handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                    errors == SslPolicyErrors.None ||
                    (cert != null && string.Equals(cert.GetCertHashString(), trustedCertThumbprint, StringComparison.OrdinalIgnoreCase))
            };
        }

        using var client = handler is not null
            ? new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri(baseUrl) }
            : new HttpClient { BaseAddress = new Uri(baseUrl) };
        client.Timeout = TimeSpan.FromSeconds(10);

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync("/api/version", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return $"Cannot reach service: {ex.InnerException?.Message ?? ex.Message}";
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return "The service does not support protocol versioning. Please update the service to the latest version.";
        }

        if (!response.IsSuccessStatusCode)
        {
            return $"Version check failed — service returned {(int)response.StatusCode}.";
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var versionDoc = JsonDocument.Parse(json);
        if (versionDoc.RootElement.TryGetProperty("protocolVersion", out var versionProp))
        {
            var remoteVersion = versionProp.GetInt32();
            if (remoteVersion < ServiceProtocol.Current)
            {
                return $"The service is running protocol version {remoteVersion} but this client requires version {ServiceProtocol.Current}. Please update the service.";
            }
        }

        return null;
    }

    /// <summary>
    /// Loads initial snapshots from the service for all monitored groups.
    /// </summary>
    private async Task LoadCurrentSnapshotsAsync()
    {
        if (_hubConnection?.State != HubConnectionState.Connected) return;

        try
        {
            var snapshots = await _hubConnection.InvokeAsync<IReadOnlyList<MonitoredGroupSnapshot>>(
                "GetCurrentSnapshots");

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var snapshot in snapshots)
                    OnSnapshotReceived(snapshot.Name, snapshot);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load current snapshots from service");
        }
    }

    /// <summary>
    /// Invokes the hub method to get historical snapshot data for the statistics view.
    /// </summary>
    public async Task<IReadOnlyList<SnapshotDataPoint>> GetSnapshotHistoryAsync(
        DateTimeOffset since, DateTimeOffset until,
        string? groupName = null, string? replicaName = null, string? databaseName = null,
        CancellationToken cancellationToken = default)
    {
        if (_hubConnection?.State != HubConnectionState.Connected)
            return Array.Empty<SnapshotDataPoint>();

        return await _hubConnection.InvokeAsync<IReadOnlyList<SnapshotDataPoint>>(
            "GetSnapshotHistory", since, until, groupName, replicaName, databaseName,
            cancellationToken);
    }

    /// <summary>
    /// Invokes the hub method to get distinct filter values for dropdowns.
    /// </summary>
    public async Task<SnapshotFilterOptions> GetSnapshotFiltersAsync(
        string? groupName = null, string? replicaName = null,
        CancellationToken cancellationToken = default)
    {
        if (_hubConnection?.State != HubConnectionState.Connected)
            return new SnapshotFilterOptions
            {
                GroupNames = Array.Empty<string>(),
                ReplicaNames = Array.Empty<string>(),
                DatabaseNames = Array.Empty<string>()
            };

        return await _hubConnection.InvokeAsync<SnapshotFilterOptions>(
            "GetSnapshotFilters", groupName, replicaName, cancellationToken);
    }

    /// <summary>
    /// Invokes the hub method to get alert history events.
    /// </summary>
    public async Task<IReadOnlyList<AlertEvent>> GetAlertHistoryAsync(
        string? groupName = null, DateTimeOffset? since = null, int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (_hubConnection?.State != HubConnectionState.Connected)
            return Array.Empty<AlertEvent>();

        return await _hubConnection.InvokeAsync<IReadOnlyList<AlertEvent>>(
            "GetAlertHistory", groupName, since, Math.Clamp(limit, 1, 5000),
            cancellationToken);
    }

    /// <summary>
    /// Invokes the hub method to export snapshot data as an Excel byte array.
    /// </summary>
    public async Task<byte[]> ExportToExcelAsync(
        DateTimeOffset since, DateTimeOffset until,
        string? groupName = null, string? replicaName = null, string? databaseName = null,
        CancellationToken cancellationToken = default)
    {
        if (_hubConnection?.State != HubConnectionState.Connected)
            return Array.Empty<byte>();

        return await _hubConnection.InvokeAsync<byte[]>(
            "ExportToExcel", since, until, groupName, replicaName, databaseName,
            cancellationToken);
    }

    /// <summary>
    /// Returns the most recent snapshot for each monitored group.
    /// </summary>
    public IReadOnlyList<MonitoredGroupSnapshot> GetLatestSnapshots()
        => _latestSnapshots.Values.ToList().AsReadOnly();

    public MonitorTabViewModel? FindTab(string name)
    {
        foreach (var tab in MonitorTabs)
        {
            if (string.Equals(tab.TabTitle, name, StringComparison.OrdinalIgnoreCase))
                return tab;
        }
        return null;
    }

    /// <summary>
    /// No-op in service-client mode. Hub callbacks are wired during <see cref="ConnectAsync"/>.
    /// </summary>
    public void SubscribeToMonitors() { }

    /// <summary>
    /// In service-client mode, the service manages groups. This loads the initial state.
    /// </summary>
    public async Task LoadAndStartAsync()
    {
        await LoadCurrentSnapshotsAsync();
    }

    /// <summary>
    /// Not applicable in service-client mode — the service manages SQL connections.
    /// </summary>
    public Task StartGroupAsync(string groupName, AvailabilityGroupType groupType)
    {
        _logger.LogDebug("StartGroupAsync ignored in service-client mode for {Group}", groupName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Not applicable in service-client mode — the service manages SQL connections.
    /// </summary>
    public Task StopGroupAsync(string groupName, AvailabilityGroupType groupType)
    {
        _logger.LogDebug("StopGroupAsync ignored in service-client mode for {Group}", groupName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// In service-client mode, returns the last cached snapshot for the group.
    /// Real-time polling is handled server-side.
    /// </summary>
    public Task<MonitoredGroupSnapshot> PollOnceAsync(string groupName, AvailabilityGroupType groupType)
    {
        if (_latestSnapshots.TryGetValue(groupName, out var cached))
            return Task.FromResult(cached);

        return Task.FromResult(new MonitoredGroupSnapshot
        {
            Name = groupName,
            GroupType = groupType,
            Timestamp = DateTimeOffset.UtcNow,
            IsConnected = false,
            OverallHealth = SynchronizationHealth.Unknown,
            ErrorMessage = "No snapshot available yet — waiting for service push."
        });
    }

    /// <summary>
    /// Disconnects the SignalR hub connection.
    /// </summary>
    public Task DisposeMonitorsAsync() => DisconnectAsync();

    public async Task DisconnectAsync()
    {
        _reconnectCts?.Cancel();
        if (_hubConnection is not null)
        {
            try { await _hubConnection.StopAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping hub connection"); }
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
    }

    public void Dispose()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        if (_hubConnection is not null)
        {
            try { _hubConnection.StopAsync().GetAwaiter().GetResult(); }
            catch { /* shutting down */ }
            _hubConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void WireHubCallbacks(HubConnection connection)
    {
        connection.On<string, MonitoredGroupSnapshot>("OnSnapshotReceived",
            (groupName, snapshot) =>
            {
                Dispatcher.UIThread.Post(() => OnSnapshotReceived(groupName, snapshot));
            });

        connection.On<AlertEvent>("OnAlertFired",
            alert =>
            {
                Dispatcher.UIThread.Post(() => AlertRaised?.Invoke(alert));
            });

        connection.On<string, string>("OnConnectionStateChanged",
            (groupName, state) =>
            {
                _logger.LogInformation("Group {Group} connection state: {State}", groupName, state);
            });
    }

    private void OnSnapshotReceived(string groupName, MonitoredGroupSnapshot snapshot)
    {
        var existing = FindTab(groupName);
        if (existing is null)
        {
            existing = new MonitorTabViewModel { TabTitle = groupName, GroupType = snapshot.GroupType };
            MonitorTabs.Add(existing);
        }

        existing.ApplySnapshot(snapshot);
        _latestSnapshots[groupName] = snapshot;

        SnapshotProcessed?.Invoke(snapshot);
    }

    /// <summary>
    /// Reconnect policy with exponential backoff capped at 60 seconds.
    /// </summary>
    private sealed class RetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] DelayPattern =
        {
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60)
        };

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = Math.Min(retryContext.PreviousRetryCount, DelayPattern.Length - 1);
            return DelayPattern[index];
        }
    }
}
