using SqlAgMonitor.Core;
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

// SignalR
builder.Services.AddSignalR();

// Headless monitoring coordinator
builder.Services.AddHostedService<MonitoringWorker>();

// Logging
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});

var app = builder.Build();

app.MapHub<MonitorHub>("/monitor");

app.Run();
