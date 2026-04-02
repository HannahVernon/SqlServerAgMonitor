using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reactive;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using ReactiveUI;

namespace SqlAgMonitor.Installer.ViewModels;

public class InstallerViewModel : ReactiveObject
{
    private const string ServiceName = "SqlAgMonitorService";
    private const string DisplayName = "SQL Server AG Monitor Service";
    private const string Publisher = "SqlAgMonitor";
    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SqlAgMonitorService";

    // Wizard state
    private int _currentStep;
    private bool _isInstalling;
    private bool _isComplete;
    private bool _hasFailed;

    // Step 1: Install path
    private string _installPath = @"C:\Program Files\SqlAgMonitor";

    // Step 2: Service account
    private bool _useLocalService = true;
    private string _serviceAccount = @"NT AUTHORITY\LOCAL SERVICE";
    private string _servicePassword = string.Empty;

    // Step 3: Port
    private int _port = 58432;

    // Step 4: TLS
    private bool _useTls;
    private string _certificatePath = string.Empty;

    // Step 5: Admin credentials
    private string _adminUsername = "admin";
    private string _adminPassword = string.Empty;
    private string _adminPasswordConfirm = string.Empty;

    // Progress
    private string _progressText = string.Empty;
    private double _progressValue;
    private string _errorMessage = string.Empty;

    public InstallerViewModel()
    {
        var canGoNext = this.WhenAnyValue(
            x => x.CurrentStep, x => x.IsInstalling,
            (step, installing) => step < 5 && !installing);

        var canGoBack = this.WhenAnyValue(
            x => x.CurrentStep, x => x.IsInstalling,
            (step, installing) => step > 0 && !installing);

        var canInstall = this.WhenAnyValue(
            x => x.CurrentStep, x => x.IsInstalling,
            x => x.AdminPassword, x => x.AdminPasswordConfirm,
            (step, installing, pw, confirm) =>
                step == 5 && !installing &&
                !string.IsNullOrWhiteSpace(pw) && pw.Length >= 8 && pw == confirm);

        NextCommand = ReactiveCommand.Create(OnNext, canGoNext);
        BackCommand = ReactiveCommand.Create(OnBack, canGoBack);
        InstallCommand = ReactiveCommand.CreateFromTask(OnInstallAsync, canInstall);
        CloseCommand = ReactiveCommand.Create(OnClose);
    }

    // Commands
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public event Action? CloseRequested;

    // Wizard navigation
    public int CurrentStep { get => _currentStep; set => this.RaiseAndSetIfChanged(ref _currentStep, value); }
    public bool IsInstalling { get => _isInstalling; set => this.RaiseAndSetIfChanged(ref _isInstalling, value); }
    public bool IsComplete { get => _isComplete; set => this.RaiseAndSetIfChanged(ref _isComplete, value); }
    public bool HasFailed { get => _hasFailed; set => this.RaiseAndSetIfChanged(ref _hasFailed, value); }

    // Step 1
    public string InstallPath { get => _installPath; set => this.RaiseAndSetIfChanged(ref _installPath, value); }

    // Step 2
    public bool UseLocalService
    {
        get => _useLocalService;
        set
        {
            this.RaiseAndSetIfChanged(ref _useLocalService, value);
            if (value) ServiceAccount = @"NT AUTHORITY\LOCAL SERVICE";
        }
    }
    public string ServiceAccount { get => _serviceAccount; set => this.RaiseAndSetIfChanged(ref _serviceAccount, value); }
    public string ServicePassword { get => _servicePassword; set => this.RaiseAndSetIfChanged(ref _servicePassword, value); }

    // Step 3
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, Math.Clamp(value, 1024, 65535)); }

    // Step 4
    public bool UseTls { get => _useTls; set => this.RaiseAndSetIfChanged(ref _useTls, value); }
    public string CertificatePath { get => _certificatePath; set => this.RaiseAndSetIfChanged(ref _certificatePath, value); }

    // Step 5
    public string AdminUsername { get => _adminUsername; set => this.RaiseAndSetIfChanged(ref _adminUsername, value); }
    public string AdminPassword { get => _adminPassword; set => this.RaiseAndSetIfChanged(ref _adminPassword, value); }
    public string AdminPasswordConfirm { get => _adminPasswordConfirm; set => this.RaiseAndSetIfChanged(ref _adminPasswordConfirm, value); }

    // Progress
    public string ProgressText { get => _progressText; set => this.RaiseAndSetIfChanged(ref _progressText, value); }
    public double ProgressValue { get => _progressValue; set => this.RaiseAndSetIfChanged(ref _progressValue, value); }
    public string ErrorMessage { get => _errorMessage; set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }

    private void OnNext() => CurrentStep++;
    private void OnBack() => CurrentStep--;
    private void OnClose() => CloseRequested?.Invoke();

    private async Task OnInstallAsync()
    {
        IsInstalling = true;
        HasFailed = false;
        ErrorMessage = string.Empty;

        try
        {
            // Step 1: Publish service
            await SetProgress("Publishing service files...", 0);
            await PublishServiceAsync();

            // Step 2: Write appsettings override
            await SetProgress("Writing configuration...", 20);
            WriteAppSettings();

            // Step 3: Create Windows Service
            await SetProgress("Creating Windows Service...", 40);
            await CreateServiceAsync();

            // Step 4: Start service
            await SetProgress("Starting service...", 60);
            await StartServiceAsync();

            // Step 5: Create initial admin user
            await SetProgress("Creating admin user...", 80);
            await CreateAdminUserAsync();

            // Step 6: Write Add/Remove Programs registry
            await SetProgress("Registering in Add/Remove Programs...", 90);
            WriteUninstallRegistry();

            await SetProgress("Installation complete!", 100);
            IsComplete = true;
            CurrentStep = 6;
        }
        catch (Exception ex)
        {
            HasFailed = true;
            ErrorMessage = ex.Message;
            ProgressText = "Installation failed.";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private Task SetProgress(string text, double value)
    {
        ProgressText = text;
        ProgressValue = value;
        return Task.Delay(200); // brief pause so UI updates
    }

    private async Task PublishServiceAsync()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "SqlAgMonitor.Service", "SqlAgMonitor.Service.csproj");

        if (!File.Exists(projectPath))
            throw new FileNotFoundException($"Service project not found at {projectPath}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{projectPath}\" --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output \"{InstallPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet publish.");

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"dotnet publish failed (exit code {process.ExitCode}): {stderr}");
        }
    }

    private void WriteAppSettings()
    {
        var settings = new
        {
            Service = new
            {
                Port = Port
            },
            Logging = new
            {
                LogLevel = new
                {
                    Default = "Information"
                }
            }
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var settingsPath = Path.Combine(InstallPath, "appsettings.json");
        File.WriteAllText(settingsPath, json);
    }

    private async Task CreateServiceAsync()
    {
        var exePath = Path.Combine(InstallPath, "SqlAgMonitor.Service.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Service executable not found at {exePath}");

        var binPath = $"\"{exePath}\"";
        var args = $"create {ServiceName} binPath= {binPath} DisplayName= \"{DisplayName}\" start= delayed-auto obj= \"{ServiceAccount}\"";

        if (!UseLocalService && !string.IsNullOrEmpty(ServicePassword))
            args += $" password= \"{ServicePassword}\"";

        var exitCode = await RunProcessAsync("sc.exe", args);
        if (exitCode != 0)
            throw new InvalidOperationException($"sc.exe create failed with exit code {exitCode}.");

        // Set description
        await RunProcessAsync("sc.exe",
            $"description {ServiceName} \"Monitors SQL Server Availability Groups and Distributed Availability Groups via SignalR.\"");

        // Configure recovery policy
        await RunProcessAsync("sc.exe",
            $"failure {ServiceName} reset= 86400 actions= restart/60000/restart/120000//");
    }

    private async Task StartServiceAsync()
    {
        var exitCode = await RunProcessAsync("sc.exe", $"start {ServiceName}");
        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to start service (exit code {exitCode}).");

        // Wait for the service to be ready
        await Task.Delay(3000);
    }

    private async Task CreateAdminUserAsync()
    {
        var scheme = UseTls ? "https" : "http";
        var baseUrl = $"{scheme}://localhost:{Port}";

        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };

        var payload = new { username = AdminUsername, password = AdminPassword };
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Retry a few times — service may still be starting
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                var response = await client.PostAsync("/api/auth/setup", content);
                if (response.IsSuccessStatusCode)
                    return;

                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    return; // User already exists — fine

                var body = await response.Content.ReadAsStringAsync();
                if (attempt == 5)
                    throw new InvalidOperationException($"Failed to create admin user: {response.StatusCode} — {body}");
            }
            catch (HttpRequestException) when (attempt < 5)
            {
                // Service not ready yet
            }

            await Task.Delay(2000);
        }
    }

    private void WriteUninstallRegistry()
    {
        var exePath = Path.Combine(InstallPath, "SqlAgMonitor.Service.exe");
        var installerPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";

        using var key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath);
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", "1.0.0");
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", InstallPath);
        key.SetValue("UninstallString", $"\"{installerPath}\" /uninstall");
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        // Estimate size in KB
        try
        {
            var dirInfo = new DirectoryInfo(InstallPath);
            var sizeKb = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length) / 1024;
            key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
        }
        catch { /* non-critical */ }
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static string FindRepoRoot()
    {
        // Walk up from the installer exe to find the repo root (has SqlAgMonitor.sln)
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "SqlAgMonitor.sln")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // Fallback: assume we're in src/SqlAgMonitor.Installer/bin/...
        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(fallback, "SqlAgMonitor.sln")))
            return fallback;

        throw new DirectoryNotFoundException("Could not find repository root (SqlAgMonitor.sln). Run the installer from within the repository.");
    }
}
