using SqlAgMonitor.Core;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Service;
using SqlAgMonitor.Service.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Run as a Windows Service when installed as one
builder.Host.UseWindowsService();

// Configure Kestrel on the service port (default 58432)
var servicePort = builder.Configuration.GetValue("Service:Port", 58432);
servicePort = Math.Clamp(servicePort, 1024, 65535);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(servicePort);
});

// Register Core services (monitoring, alerting, DuckDB, notifications, etc.)
builder.Services.AddSqlAgMonitorCore();

// SignalR with reasonable limits
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 512 * 1024; // 512 KB
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

// Headless monitoring coordinator
builder.Services.AddHostedService<MonitoringWorker>();

// File logging alongside console
var logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SqlAgMonitor", "logs");
Directory.CreateDirectory(logDirectory);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.SingleLine = true;
});
builder.Logging.SetMinimumLevel(
    builder.Configuration.GetValue("Logging:LogLevel:Default", LogLevel.Information));

var app = builder.Build();

// Initialize DuckDB schema before accepting connections
var historyService = app.Services.GetRequiredService<IHistoryMaintenanceService>();
await historyService.InitializeAsync(CancellationToken.None);

// Start the maintenance scheduler (event pruning + snapshot aggregation)
_ = app.Services.GetRequiredService<MaintenanceScheduler>();

app.MapHub<MonitorHub>("/monitor");

app.Logger.LogInformation("SqlAgMonitor Service starting on port {Port}", servicePort);

app.Run();
