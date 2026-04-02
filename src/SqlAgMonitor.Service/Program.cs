using Microsoft.AspNetCore.Authentication.JwtBearer;
using SqlAgMonitor.Core;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Service;
using SqlAgMonitor.Service.Auth;
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

// Authentication — JWT bearer tokens
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<UserStore>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Token validation parameters are set after build via JwtTokenService
        // (the signing key is loaded from disk at that point)

        // SignalR sends JWT via query string for WebSocket connections
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/monitor"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// SignalR with reasonable limits
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 512 * 1024; // 512 KB
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

// Headless monitoring coordinator — registered as singleton so the hub can access it
builder.Services.AddSingleton<MonitoringWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringWorker>());

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

// Configure JWT validation with the signing key from JwtTokenService
var jwtService = app.Services.GetRequiredService<JwtTokenService>();
var jwtOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JwtBearerOptions>>();
var bearerOptions = jwtOptions.Get(JwtBearerDefaults.AuthenticationScheme);
bearerOptions.TokenValidationParameters = jwtService.GetValidationParameters();

// Initialize DuckDB schema before accepting connections
var historyService = app.Services.GetRequiredService<IHistoryMaintenanceService>();
await historyService.InitializeAsync(CancellationToken.None);

// Start the maintenance scheduler (event pruning + snapshot aggregation)
_ = app.Services.GetRequiredService<MaintenanceScheduler>();

app.UseAuthentication();
app.UseAuthorization();

// Login endpoint — returns JWT token
app.MapPost("/api/auth/login", (LoginRequest request, JwtTokenService jwt, UserStore users) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "Username and password are required." });

    if (!users.ValidateCredentials(request.Username, request.Password))
        return Results.Unauthorized();

    var token = jwt.GenerateToken(request.Username);
    return Results.Ok(new { token });
});

// Setup endpoint — create initial admin user (only works when no users exist)
app.MapPost("/api/auth/setup", (LoginRequest request, UserStore users) =>
{
    if (users.HasUsers())
        return Results.Conflict(new { error = "Users already exist. Use login instead." });

    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "Username and password are required." });

    if (request.Password.Length < 8)
        return Results.BadRequest(new { error = "Password must be at least 8 characters." });

    users.CreateUser(request.Username, request.Password);
    return Results.Ok(new { message = $"User '{request.Username}' created." });
});

app.MapHub<MonitorHub>("/monitor").RequireAuthorization();

app.Logger.LogInformation("SqlAgMonitor Service starting on port {Port}", servicePort);

app.Run();

record LoginRequest(string Username, string Password);
