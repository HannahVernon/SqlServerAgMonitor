using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using SqlAgMonitor.Core;
using SqlAgMonitor.Core.Configuration;
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
var tlsSource = builder.Configuration.GetValue<string>("Service:Tls:Source");

builder.WebHost.ConfigureKestrel(options =>
{
    if (!string.IsNullOrEmpty(tlsSource))
    {
        options.ListenAnyIP(servicePort, listenOptions =>
        {
            if (string.Equals(tlsSource, "Store", StringComparison.OrdinalIgnoreCase))
            {
                var thumbprint = builder.Configuration.GetValue<string>("Service:Tls:Thumbprint") ?? string.Empty;
                var storeName = builder.Configuration.GetValue("Service:Tls:StoreName", "My");
                var storeLocation = builder.Configuration.GetValue("Service:Tls:StoreLocation", "LocalMachine");

                var storeLocationEnum = Enum.TryParse<StoreLocation>(storeLocation, true, out var loc)
                    ? loc : StoreLocation.LocalMachine;
                var storeNameEnum = Enum.TryParse<StoreName>(storeName, true, out var sn)
                    ? sn : StoreName.My;

                using var store = new X509Store(storeNameEnum, storeLocationEnum);
                store.Open(OpenFlags.ReadOnly);
                var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                store.Close();

                if (certs.Count > 0)
                {
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ServerCertificate = certs[0];
                        httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    });
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Certificate with thumbprint '{thumbprint}' not found in {storeLocationEnum}/{storeNameEnum}.");
                }
            }
            else if (string.Equals(tlsSource, "File", StringComparison.OrdinalIgnoreCase))
            {
                var certPath = builder.Configuration.GetValue<string>("Service:Tls:Path") ?? string.Empty;
                var fullCertPath = Path.GetFullPath(certPath);
                if (!File.Exists(fullCertPath))
                    throw new FileNotFoundException($"TLS certificate file not found: '{fullCertPath}'.");
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificate = X509CertificateLoader.LoadCertificateFromFile(fullCertPath);
                    httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                });
            }
        });
    }
    else
    {
        options.ListenAnyIP(servicePort);
    }

    // Limit request body size to 1 MB (prevents oversized config imports)
    options.Limits.MaxRequestBodySize = 1 * 1024 * 1024;
});

// Register Core services (monitoring, alerting, DuckDB, notifications, etc.)
builder.Services.AddSqlAgMonitorCore();

// Override config service to use %ProgramData%\SqlAgMonitor — a fixed,
// writable location that survives service account changes.
// (%APPDATA% varies per account, %ProgramFiles% is read-only for services)
builder.Services.AddSingleton<IConfigurationService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<JsonConfigurationService>>();
    var configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SqlAgMonitor");
    return new JsonConfigurationService(logger, configDir);
});

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

// File + console logging — logs go to %ProgramData%\SqlAgMonitor\logs\
var logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "SqlAgMonitor", "logs");
Directory.CreateDirectory(logDirectory);
var logFilePath = Path.Combine(logDirectory, $"service-{DateTime.Now:yyyy-MM-dd}.log");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.SingleLine = true;
});
builder.Logging.AddProvider(new FileLoggerProvider(logFilePath));
builder.Logging.SetMinimumLevel(
    builder.Configuration.GetValue("Logging:LogLevel:Default", LogLevel.Information));

// Rate limiting — protect login endpoint from brute-force attacks
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", limiterOptions =>
    {
        limiterOptions.PermitLimit = 5;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

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
app.UseRateLimiter();

// Security headers — prevent clickjacking, MIME sniffing, and enforce HTTPS
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    if (context.Request.IsHttps)
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }
    await next();
});

// Global exception handler — prevents stack traces leaking to clients
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Unhandled exception on {Method} {Path}.", context.Request.Method, context.Request.Path);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "An internal server error occurred." });
        }
    }
});

// Login endpoint — returns JWT token
app.MapPost("/api/auth/login", (LoginRequest request, JwtTokenService jwt, UserStore users) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "Username and password are required." });

    if (request.Username.Length > 256 || request.Password.Length > 256)
        return Results.BadRequest(new { error = "Username and password must be 256 characters or fewer." });

    if (!users.ValidateCredentials(request.Username, request.Password))
        return Results.Unauthorized();

    var token = jwt.GenerateToken(request.Username);
    return Results.Ok(new { token });
}).RequireRateLimiting("login");

// Protocol version — unauthenticated so clients can check compatibility before login
app.MapGet("/api/version", () => Results.Ok(new
{
    protocolVersion = ServiceProtocol.Current,
    serviceName = "SqlAgMonitor.Service"
}));

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

// Configuration export — returns sanitised subset (credential keys redacted)
app.MapGet("/api/config/export", (IConfigurationService configService) =>
{
    var config = configService.Load();

    var redactedGroups = config.MonitoredGroups.Select(g => new MonitoredGroupConfig
    {
        Name = g.Name,
        GroupType = g.GroupType,
        PollingIntervalSeconds = g.PollingIntervalSeconds,
        Connections = g.Connections.Select(c => new ConnectionConfig
        {
            Server = c.Server,
            AuthType = c.AuthType,
            Username = c.Username,
            CredentialKey = null,
            Encrypt = c.Encrypt,
            TrustServerCertificate = c.TrustServerCertificate
        }).ToList(),
        AlertOverrides = g.AlertOverrides,
        MutedAlerts = g.MutedAlerts
    }).ToList();

    var redactedEmail = new EmailSettings
    {
        Enabled = config.Email.Enabled,
        SmtpServer = config.Email.SmtpServer,
        SmtpPort = config.Email.SmtpPort,
        UseTls = config.Email.UseTls,
        FromAddress = config.Email.FromAddress,
        ToAddresses = config.Email.ToAddresses,
        Username = config.Email.Username,
        CredentialKey = null
    };

    return Results.Ok(new ConfigExportResponse(redactedGroups, config.Alerts, redactedEmail, config.Syslog));
}).RequireAuthorization();

// Configuration import — merges incoming sections into existing config
app.MapPost("/api/config/import", (ConfigImportRequest request, IConfigurationService configService) =>
{
    // Validate import payload — enforce count limits to prevent resource exhaustion
    const int maxGroups = 50;
    const int maxConnectionsPerGroup = 10;

    if (request.MonitoredGroups is { Count: > maxGroups })
        return Results.BadRequest(new { error = $"Too many groups ({request.MonitoredGroups.Count}). Maximum is {maxGroups}." });

    var config = configService.Load();

    int groupCount = 0;
    bool alertsImported = false;
    bool emailImported = false;
    bool syslogImported = false;

    if (request.MonitoredGroups is { Count: > 0 })
    {
        foreach (var incoming in request.MonitoredGroups)
        {
            if (string.IsNullOrWhiteSpace(incoming.Name) || incoming.Name.Length > 256)
                return Results.BadRequest(new { error = "Group name is required and must be 256 characters or fewer." });

            if (incoming.Connections.Count > maxConnectionsPerGroup)
                return Results.BadRequest(new { error = $"Group '{incoming.Name}' has too many connections ({incoming.Connections.Count}). Maximum is {maxConnectionsPerGroup}." });

            // Clamp polling interval to sane range
            if (incoming.PollingIntervalSeconds.HasValue)
                incoming.PollingIntervalSeconds = Math.Clamp(incoming.PollingIntervalSeconds.Value, 5, 3600);

            // Validate connection fields
            foreach (var conn in incoming.Connections)
            {
                if (string.IsNullOrWhiteSpace(conn.Server) || conn.Server.Length > 512)
                    return Results.BadRequest(new { error = $"Connection server in group '{incoming.Name}' is invalid." });
            }

            var existing = config.MonitoredGroups
                .FirstOrDefault(g => string.Equals(g.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.GroupType = incoming.GroupType;
                existing.PollingIntervalSeconds = incoming.PollingIntervalSeconds;
                existing.Connections = incoming.Connections;
                existing.AlertOverrides = incoming.AlertOverrides;
                existing.MutedAlerts = incoming.MutedAlerts;
            }
            else
            {
                config.MonitoredGroups.Add(incoming);
            }

            groupCount++;
        }
    }

    if (request.Alerts is not null)
    {
        config.Alerts = request.Alerts;
        alertsImported = true;
    }

    if (request.Email is not null)
    {
        // Validate SMTP settings
        if (!string.IsNullOrWhiteSpace(request.Email.SmtpServer) && request.Email.SmtpServer.Length > 512)
            return Results.BadRequest(new { error = "SMTP server name is too long." });
        request.Email.SmtpPort = Math.Clamp(request.Email.SmtpPort, 1, 65535);

        config.Email = request.Email;
        emailImported = true;
    }

    if (request.Syslog is not null)
    {
        config.Syslog = request.Syslog;
        syslogImported = true;
    }

    configService.Save(config);

    return Results.Ok(new ConfigImportResponse(
        new ConfigImportResult(groupCount, alertsImported, emailImported, syslogImported)));
}).RequireAuthorization();

app.MapHub<MonitorHub>("/monitor").RequireAuthorization();

var serviceVersion = (System.Reflection.CustomAttributeExtensions
    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MonitorHub).Assembly))
    ?.InformationalVersion ?? "0.0.0";
app.Logger.LogInformation("SqlAgMonitor Service v{Version} starting on port {Port}", serviceVersion, servicePort);

app.Run();

record LoginRequest(string Username, string Password);

record ConfigExportResponse(
    List<MonitoredGroupConfig> MonitoredGroups,
    AlertSettings Alerts,
    EmailSettings Email,
    SyslogSettings Syslog);

record ConfigImportRequest(
    List<MonitoredGroupConfig>? MonitoredGroups,
    AlertSettings? Alerts,
    EmailSettings? Email,
    SyslogSettings? Syslog);

record ConfigImportResponse(ConfigImportResult Imported);
record ConfigImportResult(int Groups, bool Alerts, bool Email, bool Syslog);
