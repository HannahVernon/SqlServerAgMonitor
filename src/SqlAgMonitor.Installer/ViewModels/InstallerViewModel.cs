using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using ReactiveUI;

namespace SqlAgMonitor.Installer.ViewModels;

public class InstallerViewModel : ReactiveObject
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlAgMonitor", "installer.log");

    private const string ServiceName = "SqlAgMonitorService";
    private const string DisplayName = "SQL Server AG Monitor Service";
    private const string Publisher = "SqlAgMonitor";
    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SqlAgMonitorService";

    // Wizard state
    private int _currentStep;
    private bool _isInstalling;
    private bool _isComplete;
    private bool _hasFailed;
    private bool _isUpgrade;
    private bool _skipAdminSetup;
    private bool _keepExistingSettings = true;
    private readonly List<string> _completedActions = new();

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
    private bool _useStoreCertificate = true;
    private CertificateEntry? _selectedCertificate;
    private string _certificatePath = string.Empty;
    private string _certificateWarning = string.Empty;
    private bool _isSelfSignedCert;
    private bool _trustSelfSignedCert;

    // Step 5: Firewall
    private bool _createFirewallRule = true;
    private bool _firewallAllowAnySource = true;
    private string _firewallRemoteAddress = string.Empty;

    // Step 6: Admin credentials
    private string _adminUsername = "admin";
    private string _adminPassword = string.Empty;
    private string _adminPasswordConfirm = string.Empty;

    // Progress
    private string _progressText = string.Empty;
    private double _progressValue;
    private string _errorMessage = string.Empty;

    // Grant script output
    private string _grantScriptPath = string.Empty;

    public InstallerViewModel()
    {
        var canGoNext = this.WhenAnyValue(
            x => x.CurrentStep, x => x.IsInstalling,
            (step, installing) => step < 7 && !installing);

        var canGoBack = this.WhenAnyValue(
            x => x.CurrentStep, x => x.IsInstalling,
            (step, installing) => step > 0 && !installing);

        var canInstall = this.WhenAnyValue(
            x => x.CurrentStep, x => x.IsInstalling,
            x => x.AdminPassword, x => x.AdminPasswordConfirm,
            x => x.SkipAdminSetup, x => x.KeepExistingSettings,
            (step, installing, pw, confirm, skipAdmin, keepExisting) =>
                step == 7 && !installing &&
                ((skipAdmin && keepExisting) ||
                 (!string.IsNullOrWhiteSpace(pw) && pw.Length >= 8 && pw == confirm)));

        NextCommand = ReactiveCommand.Create(OnNext, canGoNext);
        BackCommand = ReactiveCommand.Create(OnBack, canGoBack);
        InstallCommand = ReactiveCommand.CreateFromTask(OnInstallAsync, canInstall);
        CloseCommand = ReactiveCommand.CreateFromTask(OnCloseAsync);

        var canViewCert = this.WhenAnyValue(x => x.SelectedCertificate)
            .Select(cert => cert != null);
        ViewCertificateCommand = ReactiveCommand.Create(ViewCertificateDetails, canViewCert);

        var canCopyError = this.WhenAnyValue(x => x.ErrorMessage)
            .Select(msg => !string.IsNullOrEmpty(msg));
        CopyErrorCommand = ReactiveCommand.CreateFromTask(OnCopyErrorAsync, canCopyError);

        var canCopyScript = this.WhenAnyValue(x => x.GrantScriptPath)
            .Select(p => !string.IsNullOrEmpty(p));
        CopyGrantScriptCommand = ReactiveCommand.CreateFromTask(OnCopyGrantScriptAsync, canCopyScript);
    }

    public async Task DetectExistingInstallationAsync()
    {
        var existingConfig = QueryServiceConfigWin32();
        if (existingConfig == null) return;

        IsUpgrade = true;
        Log($"Detected existing installation: BinPath={existingConfig.BinPath}, Account={existingConfig.ServiceAccount}");

        var existingBinPath = existingConfig.BinPath.Trim('"');
        var existingDir = Path.GetDirectoryName(existingBinPath);
        if (!string.IsNullOrEmpty(existingDir))
            InstallPath = existingDir;

        var account = existingConfig.ServiceAccount;
        if (string.Equals(account, @"NT AUTHORITY\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            UseLocalService = true;
        }
        else
        {
            UseLocalService = false;
            ServiceAccount = account;
        }

        var appSettingsPath = existingDir != null
            ? Path.Combine(existingDir, "appsettings.json")
            : null;

        if (appSettingsPath != null && File.Exists(appSettingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(appSettingsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("Service", out var svc))
                {
                    if (svc.TryGetProperty("Port", out var portEl) && portEl.TryGetInt32(out var port))
                        Port = port;

                    if (svc.TryGetProperty("Tls", out var tls))
                    {
                        UseTls = true;

                        if (tls.TryGetProperty("Source", out var src)
                            && string.Equals(src.GetString(), "Store", StringComparison.OrdinalIgnoreCase))
                        {
                            UseStoreCertificate = true;
                            LoadStoreCertificates();

                            if (tls.TryGetProperty("Thumbprint", out var tp))
                            {
                                var thumbprint = tp.GetString();
                                SelectedCertificate = StoreCertificates
                                    .FirstOrDefault(c => string.Equals(c.Thumbprint, thumbprint,
                                        StringComparison.OrdinalIgnoreCase));
                            }
                        }
                        else if (tls.TryGetProperty("Source", out var fileSrc)
                                 && string.Equals(fileSrc.GetString(), "File", StringComparison.OrdinalIgnoreCase))
                        {
                            UseStoreCertificate = false;
                            if (tls.TryGetProperty("Path", out var pathEl))
                                CertificatePath = pathEl.GetString() ?? string.Empty;
                        }
                    }
                }

                Log("Pre-populated settings from existing appsettings.json");
            }
            catch (Exception ex)
            {
                Log($"Failed to read existing appsettings.json: {ex.Message}");
            }
        }

        SkipAdminSetup = true;
        Log("Upgrade mode — admin setup will be skipped (existing admin user preserved)");
    }

    // Commands
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> ViewCertificateCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyErrorCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyGrantScriptCommand { get; }

    public event Action? CloseRequested;
    public Func<string, Task>? CopyToClipboard { get; set; }

    // Wizard navigation
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
            this.RaisePropertyChanged(nameof(IsStep4));
            this.RaisePropertyChanged(nameof(IsStep5));
            this.RaisePropertyChanged(nameof(IsStep6));
            this.RaisePropertyChanged(nameof(IsStep7));
            this.RaisePropertyChanged(nameof(IsStep8));
        }
    }

    // Step visibility — avoids ObjectConverters.Equal int/string mismatch
    public bool IsStep0 => CurrentStep == 0;
    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    public bool IsStep5 => CurrentStep == 5;
    public bool IsStep6 => CurrentStep == 6;
    public bool IsStep7 => CurrentStep == 7;
    public bool IsStep8 => CurrentStep == 8;

    public bool IsInstalling { get => _isInstalling; set => this.RaiseAndSetIfChanged(ref _isInstalling, value); }
    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            this.RaiseAndSetIfChanged(ref _isComplete, value);
            this.RaisePropertyChanged(nameof(CloseButtonText));
        }
    }
    public bool HasFailed { get => _hasFailed; set => this.RaiseAndSetIfChanged(ref _hasFailed, value); }
    public bool IsUpgrade
    {
        get => _isUpgrade;
        set
        {
            this.RaiseAndSetIfChanged(ref _isUpgrade, value);
            this.RaisePropertyChanged(nameof(InstallButtonText));
        }
    }
    public bool SkipAdminSetup { get => _skipAdminSetup; set => this.RaiseAndSetIfChanged(ref _skipAdminSetup, value); }
    public bool KeepExistingSettings
    {
        get => _keepExistingSettings;
        set
        {
            this.RaiseAndSetIfChanged(ref _keepExistingSettings, value);
            if (!value)
            {
                SkipAdminSetup = false;
                this.RaisePropertyChanged(nameof(InstallButtonText));
            }
            else if (IsUpgrade)
            {
                SkipAdminSetup = true;
                this.RaisePropertyChanged(nameof(InstallButtonText));
            }
        }
    }

    public string AppVersion => GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

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
    public int Port
    {
        get => _port;
        set
        {
            this.RaiseAndSetIfChanged(ref _port, Math.Clamp(value, 1024, 65535));
            this.RaisePropertyChanged(nameof(FirewallCheckboxText));
        }
    }

    // Step 4
    public bool UseTls
    {
        get => _useTls;
        set
        {
            this.RaiseAndSetIfChanged(ref _useTls, value);
            if (value && StoreCertificates.Count == 0)
                LoadStoreCertificates();
        }
    }
    public bool UseStoreCertificate
    {
        get => _useStoreCertificate;
        set => this.RaiseAndSetIfChanged(ref _useStoreCertificate, value);
    }
    public ObservableCollection<CertificateEntry> StoreCertificates { get; } = new();
    public CertificateEntry? SelectedCertificate
    {
        get => _selectedCertificate;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCertificate, value);
            ValidateSelectedCertificateKey();
        }
    }
    public string CertificatePath { get => _certificatePath; set => this.RaiseAndSetIfChanged(ref _certificatePath, value); }
    public string CertificateWarning { get => _certificateWarning; set => this.RaiseAndSetIfChanged(ref _certificateWarning, value); }
    public bool IsSelfSignedCert
    {
        get => _isSelfSignedCert;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelfSignedCert, value);
            this.RaisePropertyChanged(nameof(SelfSignedNotTrusted));
        }
    }
    public bool TrustSelfSignedCert
    {
        get => _trustSelfSignedCert;
        set
        {
            this.RaiseAndSetIfChanged(ref _trustSelfSignedCert, value);
            this.RaisePropertyChanged(nameof(SelfSignedNotTrusted));
        }
    }
    public bool SelfSignedNotTrusted => IsSelfSignedCert && !TrustSelfSignedCert;

    /// <summary>Selected certificate thumbprint — from store selection or empty if using .pfx file.</summary>
    public string SelectedThumbprint => SelectedCertificate?.Thumbprint ?? string.Empty;

    // Step 5: Firewall
    public bool CreateFirewallRule
    {
        get => _createFirewallRule;
        set => this.RaiseAndSetIfChanged(ref _createFirewallRule, value);
    }
    public bool FirewallAllowAnySource
    {
        get => _firewallAllowAnySource;
        set => this.RaiseAndSetIfChanged(ref _firewallAllowAnySource, value);
    }
    public string FirewallRemoteAddress
    {
        get => _firewallRemoteAddress;
        set => this.RaiseAndSetIfChanged(ref _firewallRemoteAddress, value);
    }
    public string FirewallCheckboxText => $"Create Windows Firewall rule for inbound TCP port {Port}";

    // Step 6
    public string AdminUsername { get => _adminUsername; set => this.RaiseAndSetIfChanged(ref _adminUsername, value); }
    public string AdminPassword { get => _adminPassword; set => this.RaiseAndSetIfChanged(ref _adminPassword, value); }
    public string AdminPasswordConfirm { get => _adminPasswordConfirm; set => this.RaiseAndSetIfChanged(ref _adminPasswordConfirm, value); }

    public string CloseButtonText => IsComplete ? "Close" : "Cancel";
    public string InstallButtonText => IsUpgrade && KeepExistingSettings ? "Upgrade" : "Install";

    // Progress
    public string ProgressText { get => _progressText; set => this.RaiseAndSetIfChanged(ref _progressText, value); }
    public double ProgressValue { get => _progressValue; set => this.RaiseAndSetIfChanged(ref _progressValue, value); }
    public string ErrorMessage { get => _errorMessage; set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }

    // Grant script
    public string GrantScriptPath { get => _grantScriptPath; set => this.RaiseAndSetIfChanged(ref _grantScriptPath, value); }
    public bool HasGrantScript => !string.IsNullOrEmpty(GrantScriptPath);

    private void OnNext()
    {
        var next = CurrentStep + 1;
        if (SkipAdminSetup && KeepExistingSettings && next == 6)
            next = 7;
        CurrentStep = next;
    }
    private void OnBack()
    {
        var prev = CurrentStep - 1;
        if (SkipAdminSetup && KeepExistingSettings && prev == 6)
            prev = 5;
        CurrentStep = prev;
    }

    private async Task OnCopyErrorAsync()
    {
        if (CopyToClipboard != null && !string.IsNullOrEmpty(ErrorMessage))
            await CopyToClipboard(ErrorMessage);
    }

    private async Task OnCopyGrantScriptAsync()
    {
        if (CopyToClipboard != null && !string.IsNullOrEmpty(GrantScriptPath))
        {
            try
            {
                var scriptContent = File.ReadAllText(GrantScriptPath);
                await CopyToClipboard(scriptContent);
            }
            catch { /* best effort */ }
        }
    }

    private async Task OnCloseAsync()
    {
        if (IsComplete || _completedActions.Count == 0)
        {
            CloseRequested?.Invoke();
            return;
        }

        var summary = string.Join("\n", _completedActions.Select(a => $"  • {a}"));
        var message = _completedActions.Count > 0
            ? $"The following actions have already been completed:\n\n{summary}\n\n" +
              "If you cancel now, you may need to manually clean up:\n" +
              $"  • Delete the installed files at: {InstallPath}\n" +
              $"  • Remove the Windows Service: sc.exe delete {ServiceName}\n" +
              $"  • Remove the registry entry from Add/Remove Programs\n\n" +
              "Are you sure you want to cancel?"
            : "No installation steps have been performed yet.\n\nAre you sure you want to cancel?";

        if (ConfirmCancelInstallation != null)
        {
            var confirmed = await ConfirmCancelInstallation(message);
            if (!confirmed) return;
        }

        CloseRequested?.Invoke();
    }

    private async Task OnInstallAsync()
    {
        IsInstalling = true;
        HasFailed = false;
        ErrorMessage = string.Empty;
        _completedActions.Clear();

        Log("=== Installation started ===");

        // Normalize install path to prevent symlink/junction attacks
        InstallPath = Path.GetFullPath(InstallPath);
        Log($"InstallPath={InstallPath}, Port={Port}, UseTls={UseTls}, ServiceAccount={ServiceAccount}");

        try
        {
            await SetProgress("Checking for existing installation...", 0);
            ValidateInstallPath();

            await SetProgress("Publishing service files...", 5);
            await PublishServiceAsync();
            _completedActions.Add($"Published service files to {InstallPath}");

            await SetProgress("Writing configuration...", 20);
            WriteAppSettings();
            _completedActions.Add("Wrote appsettings.json configuration");
            Log("Wrote appsettings.json");

            if (UseTls && UseStoreCertificate && SelectedCertificate != null)
            {
                await SetProgress("Configuring certificate permissions...", 30);
                await GrantCertificatePrivateKeyAccessAsync();
            }

            await SetProgress("Creating Windows Service...", 40);
            await CreateServiceAsync();
            _completedActions.Add($"Created Windows Service '{ServiceName}'");
            Log($"Created service '{ServiceName}'");

            await SetProgress("Starting service...", 55);
            await StartServiceAsync();
            _completedActions.Add("Started the Windows Service");
            Log("Service started");

            if (CreateFirewallRule)
            {
                await SetProgress("Configuring Windows Firewall...", 65);
                await CreateFirewallRuleAsync();
                _completedActions.Add($"Created firewall rule for TCP port {Port}");
                Log($"Created firewall rule for port {Port}");
            }

            if (!(SkipAdminSetup && KeepExistingSettings))
            {
                await SetProgress("Creating admin user...", 80);
                await CreateAdminUserAsync();
                _completedActions.Add($"Created admin user '{AdminUsername}'");
                Log($"Created admin user '{AdminUsername}'");
            }
            else
            {
                Log("Skipped admin user creation — existing admin user preserved");
            }

            await SetProgress("Registering in Add/Remove Programs...", 90);
            WriteUninstallRegistry();
            _completedActions.Add("Registered in Add/Remove Programs");
            Log("Wrote uninstall registry entry");

            await SetProgress("Generating SQL permission script...", 95);
            GenerateGrantScript();
            if (HasGrantScript)
                _completedActions.Add($"Generated grant script at {GrantScriptPath}");

            await SetProgress("Installation complete!", 100);
            IsComplete = true;
            CurrentStep = 8;
            Log("=== Installation completed successfully ===");
        }
        catch (Exception ex)
        {
            HasFailed = true;
            ErrorMessage = ex.Message;
            ProgressText = "Installation failed.";
            Log($"Installation FAILED: {ex}");
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

    private async Task CreateFirewallRuleAsync()
    {
        var ruleName = $"SqlAgMonitor Service (TCP {Port})";

        // Remove existing rule if present (idempotent)
        await RunProcessAsync("netsh", $"advfirewall firewall delete rule name=\"{ruleName}\"");

        var args = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=tcp localport={Port} profile=any";

        if (!FirewallAllowAnySource && !string.IsNullOrWhiteSpace(FirewallRemoteAddress))
        {
            var remoteAddr = FirewallRemoteAddress.Trim();
            if (!IsValidRemoteAddress(remoteAddr))
                throw new InvalidOperationException($"Invalid firewall remote address: '{remoteAddr}'. Expected IP, CIDR, or comma-separated list.");
            args += $" remoteip=\"{remoteAddr}\"";
        }

        var exitCode = await RunProcessAsync("netsh", args);
        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to create firewall rule (exit code {exitCode}).");
    }

    /// <summary>
    /// Validates that a remote address string contains only safe IP/CIDR values.
    /// Prevents command injection via the netsh argument.
    /// </summary>
    private static bool IsValidRemoteAddress(string value)
    {
        // Reject characters that could break out of the netsh argument
        const string dangerousChars = "\"'&|;`$()<>\n\r";
        if (value.IndexOfAny(dangerousChars.ToCharArray()) >= 0)
            return false;

        // Each comma-separated entry must be a valid IP or CIDR
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ipPart = entry;
            if (entry.Contains('/'))
            {
                var parts = entry.Split('/');
                if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 128)
                    return false;
                ipPart = parts[0];
            }

            if (!System.Net.IPAddress.TryParse(ipPart, out _))
                return false;
        }

        return true;
    }

    private async Task PublishServiceAsync()
    {
        await StopExistingServiceAsync();

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

        Log($"Running: dotnet {psi.Arguments}");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet publish.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Log($"dotnet publish exited with code {process.ExitCode}");
        if (!string.IsNullOrWhiteSpace(stdout)) Log($"stdout:\n{stdout}");
        if (!string.IsNullOrWhiteSpace(stderr)) Log($"stderr:\n{stderr}");

        if (process.ExitCode != 0)
        {
            var combined = string.Join("\n",
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
            throw new InvalidOperationException(
                $"dotnet publish failed (exit code {process.ExitCode}). See log at {LogPath}\n{combined}");
        }
    }

    private async Task StopExistingServiceAsync()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                Log($"Service '{ServiceName}' exists but is already stopped.");
                return;
            }

            Log($"Stopping existing service '{ServiceName}' (status: {sc.Status})...");
            await SetProgress("Stopping existing service...", 0);

            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            _completedActions.Add($"Stopped existing service '{ServiceName}' for upgrade");
            Log($"Service '{ServiceName}' stopped.");
        }
        catch (InvalidOperationException)
        {
            Log($"Service '{ServiceName}' does not exist — fresh install.");
        }
    }

    private void WriteAppSettings()
    {
        var serviceConfig = new Dictionary<string, object>
        {
            ["Port"] = Port
        };

        if (UseTls)
        {
            var tlsConfig = new Dictionary<string, object>();

            if (UseStoreCertificate && SelectedCertificate != null)
            {
                tlsConfig["Source"] = "Store";
                tlsConfig["Thumbprint"] = SelectedCertificate.Thumbprint;
                tlsConfig["StoreName"] = "My";
                tlsConfig["StoreLocation"] = "LocalMachine";
            }
            else if (!string.IsNullOrWhiteSpace(CertificatePath))
            {
                tlsConfig["Source"] = "File";
                tlsConfig["Path"] = CertificatePath;
            }

            serviceConfig["Tls"] = tlsConfig;
        }

        var settings = new Dictionary<string, object>
        {
            ["Service"] = serviceConfig,
            ["Logging"] = new Dictionary<string, object>
            {
                ["LogLevel"] = new Dictionary<string, object>
                {
                    ["Default"] = "Information"
                }
            }
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var settingsPath = Path.Combine(InstallPath, "appsettings.json");
        File.WriteAllText(settingsPath, json);
    }

    private void ValidateInstallPath()
    {
        var existingConfig = QueryServiceConfigWin32();
        if (existingConfig == null) return;

        var existingBinPath = existingConfig.BinPath.Trim('"');
        var existingDir = Path.GetDirectoryName(existingBinPath);
        var targetDir = InstallPath.TrimEnd(Path.DirectorySeparatorChar);

        if (!string.Equals(existingDir, targetDir, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Install path mismatch: existing service at \"{existingDir}\", installer targeting \"{targetDir}\"");
            throw new InvalidOperationException(
                $"The service '{ServiceName}' is already installed at:\n"
                + $"  {existingDir}\n\n"
                + $"You are attempting to install to:\n"
                + $"  {targetDir}\n\n"
                + "Installing to a different location would leave the service pointing at the old path. "
                + "Please either:\n"
                + $"  • Change the install path to \"{existingDir}\", or\n"
                + $"  • Uninstall the existing service first");
        }
    }

    private async Task CreateServiceAsync()
    {
        var exePath = Path.Combine(InstallPath, "SqlAgMonitor.Service.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Service executable not found at {exePath}");

        var expectedBinPath = $"\"{exePath}\"";
        var existingConfig = QueryServiceConfigWin32();

        if (existingConfig != null)
        {
            Log($"Existing service found: BinPath={existingConfig.BinPath}, Account={existingConfig.ServiceAccount}, StartType={existingConfig.StartType}");

            // Path mismatch is already blocked by ValidateInstallPath — only check account and start type
            var differences = new List<string>();

            if (!string.Equals(existingConfig.ServiceAccount, ServiceAccount, StringComparison.OrdinalIgnoreCase))
                differences.Add($"  Service account: \"{existingConfig.ServiceAccount}\" → \"{ServiceAccount}\"");

            var isDelayedAuto = existingConfig.StartType != null
                && existingConfig.StartType.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase)
                && existingConfig.StartType.Contains("DELAYED", StringComparison.OrdinalIgnoreCase);

            if (!isDelayedAuto)
                differences.Add($"  Start type: {existingConfig.StartType} → DELAYED AUTO_START");

            if (differences.Count == 0)
            {
                Log("Existing service config matches — skipping recreate.");
                return;
            }

            var message = $"The service '{ServiceName}' already exists with a different configuration:\n\n"
                + string.Join("\n", differences)
                + "\n\nReconfigure the service to match the installer settings?\n"
                + "Choose 'No' to keep the existing configuration.";

            Log($"Service config differences detected:\n{string.Join("\n", differences)}");

            if (ConfirmServiceReconfigure != null)
            {
                var reconfigure = await ConfirmServiceReconfigure(message);
                if (!reconfigure)
                {
                    Log("User chose to keep existing service configuration.");
                    _completedActions.Add($"Kept existing service '{ServiceName}' configuration (user choice)");
                    return;
                }
            }

            // User approved reconfiguration — try ChangeServiceConfig first
            Log($"Reconfiguring service '{ServiceName}'...");
            var password = !UseLocalService && !string.IsNullOrEmpty(ServicePassword)
                ? ServicePassword : null;
            var passwordRedacted = password != null ? "***REDACTED***" : "(null)";
            Log($"ChangeServiceConfig: obj=\"{ServiceAccount}\", password={passwordRedacted}, startType=AUTO_START");

            var reconfigured = ReconfigureServiceWin32(ServiceAccount, password);

            if (!reconfigured)
            {
                // Fallback: delete and recreate
                Log("ChangeServiceConfig failed — falling back to delete and recreate.");
                DeleteServiceWin32();
                await Task.Delay(2000);
                CreateServiceWin32(expectedBinPath, ServiceAccount, password);
            }
        }
        else
        {
            var password = !UseLocalService && !string.IsNullOrEmpty(ServicePassword)
                ? ServicePassword : null;
            var passwordRedacted = password != null ? "***REDACTED***" : "(null)";
            Log($"Creating service '{ServiceName}': binPath={expectedBinPath}, obj=\"{ServiceAccount}\", password={passwordRedacted}");

            CreateServiceWin32(expectedBinPath, ServiceAccount, password);
        }

        // Set description
        SetServiceDescription("Monitors SQL Server Availability Groups and Distributed Availability Groups via SignalR.");

        // Configure recovery policy: restart after 60s, restart after 120s, then nothing; reset after 86400s
        SetServiceFailureActions();

        // Set delayed auto-start via registry
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}", writable: true);
        if (key != null)
        {
            key.SetValue("DelayedAutostart", 1, RegistryValueKind.DWord);
            Log("Set service to delayed auto-start via registry.");
        }

        // Grant "Log on as a service" right for domain accounts
        if (!UseLocalService)
        {
            GrantLogonAsServiceRight(ServiceAccount);
            _completedActions.Add($"Granted 'Log on as a service' right to '{ServiceAccount}'");
        }
    }

    private void CreateServiceWin32(string binPath, string account, string? password)
    {
        var hSCManager = IntPtr.Zero;
        var hService = IntPtr.Zero;

        try
        {
            hSCManager = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (hSCManager == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            hService = NativeMethods.CreateService(
                hSCManager, ServiceName, DisplayName,
                NativeMethods.SERVICE_ALL_ACCESS,
                NativeMethods.SERVICE_WIN32_OWN_PROCESS,
                NativeMethods.SERVICE_AUTO_START,
                NativeMethods.SERVICE_ERROR_NORMAL,
                binPath,
                null, IntPtr.Zero, null,
                account, password);

            if (hService == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            Log($"Service '{ServiceName}' created successfully.");
        }
        finally
        {
            if (hService != IntPtr.Zero) NativeMethods.CloseServiceHandle(hService);
            if (hSCManager != IntPtr.Zero) NativeMethods.CloseServiceHandle(hSCManager);
        }
    }

    private bool ReconfigureServiceWin32(string account, string? password)
    {
        var hSCManager = IntPtr.Zero;
        var hService = IntPtr.Zero;

        try
        {
            hSCManager = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (hSCManager == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            hService = NativeMethods.OpenService(hSCManager, ServiceName, NativeMethods.SERVICE_ALL_ACCESS);
            if (hService == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var result = NativeMethods.ChangeServiceConfig(
                hService,
                NativeMethods.SERVICE_NO_CHANGE,
                NativeMethods.SERVICE_AUTO_START,
                NativeMethods.SERVICE_NO_CHANGE,
                null, null, IntPtr.Zero, null,
                account, password,
                null);

            if (!result)
            {
                var error = Marshal.GetLastWin32Error();
                Log($"ChangeServiceConfig failed with error {error}: {new System.ComponentModel.Win32Exception(error).Message}");
                return false;
            }

            Log($"Service '{ServiceName}' reconfigured successfully.");
            return true;
        }
        finally
        {
            if (hService != IntPtr.Zero) NativeMethods.CloseServiceHandle(hService);
            if (hSCManager != IntPtr.Zero) NativeMethods.CloseServiceHandle(hSCManager);
        }
    }

    private void DeleteServiceWin32()
    {
        var hSCManager = IntPtr.Zero;
        var hService = IntPtr.Zero;

        try
        {
            hSCManager = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (hSCManager == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            hService = NativeMethods.OpenService(hSCManager, ServiceName, NativeMethods.DELETE);
            if (hService == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            if (!NativeMethods.DeleteService(hService))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            Log($"Service '{ServiceName}' deleted.");
        }
        finally
        {
            if (hService != IntPtr.Zero) NativeMethods.CloseServiceHandle(hService);
            if (hSCManager != IntPtr.Zero) NativeMethods.CloseServiceHandle(hSCManager);
        }
    }

    private void SetServiceDescription(string description)
    {
        var hSCManager = IntPtr.Zero;
        var hService = IntPtr.Zero;

        try
        {
            hSCManager = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (hSCManager == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            hService = NativeMethods.OpenService(hSCManager, ServiceName, NativeMethods.SERVICE_ALL_ACCESS);
            if (hService == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var desc = new NativeMethods.SERVICE_DESCRIPTION { lpDescription = description };
            if (!NativeMethods.ChangeServiceConfig2(hService, NativeMethods.SERVICE_CONFIG_DESCRIPTION, ref desc))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            Log("Service description set.");
        }
        finally
        {
            if (hService != IntPtr.Zero) NativeMethods.CloseServiceHandle(hService);
            if (hSCManager != IntPtr.Zero) NativeMethods.CloseServiceHandle(hSCManager);
        }
    }

    private void SetServiceFailureActions()
    {
        var hSCManager = IntPtr.Zero;
        var hService = IntPtr.Zero;
        var actionsPtr = IntPtr.Zero;

        try
        {
            hSCManager = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (hSCManager == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            hService = NativeMethods.OpenService(hSCManager, ServiceName, NativeMethods.SERVICE_ALL_ACCESS);
            if (hService == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var actions = new NativeMethods.SC_ACTION[]
            {
                new() { Type = NativeMethods.SC_ACTION_RESTART, Delay = 60_000 },
                new() { Type = NativeMethods.SC_ACTION_RESTART, Delay = 120_000 },
                new() { Type = 0 /* SC_ACTION_NONE */, Delay = 0 }
            };

            var actionSize = Marshal.SizeOf<NativeMethods.SC_ACTION>();
            actionsPtr = Marshal.AllocHGlobal(actionSize * actions.Length);
            for (var i = 0; i < actions.Length; i++)
                Marshal.StructureToPtr(actions[i], actionsPtr + i * actionSize, false);

            var failureActions = new NativeMethods.SERVICE_FAILURE_ACTIONS
            {
                dwResetPeriod = 86400,
                lpRebootMsg = IntPtr.Zero,
                lpCommand = IntPtr.Zero,
                cActions = (uint)actions.Length,
                lpsaActions = actionsPtr
            };

            if (!NativeMethods.ChangeServiceConfig2(hService, NativeMethods.SERVICE_CONFIG_FAILURE_ACTIONS, ref failureActions))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            Log("Service failure actions configured (restart after 60s, restart after 120s, then none; reset after 86400s).");
        }
        finally
        {
            if (actionsPtr != IntPtr.Zero) Marshal.FreeHGlobal(actionsPtr);
            if (hService != IntPtr.Zero) NativeMethods.CloseServiceHandle(hService);
            if (hSCManager != IntPtr.Zero) NativeMethods.CloseServiceHandle(hSCManager);
        }
    }

    private void GrantLogonAsServiceRight(string accountName)
    {
        Log($"Granting 'Log on as a service' right to '{accountName}'...");

        uint sidSize = 0;
        uint domainSize = 0;
        NativeMethods.LookupAccountName(null, accountName, IntPtr.Zero, ref sidSize, null, ref domainSize, out _);

        var sidPtr = Marshal.AllocHGlobal((int)sidSize);
        var domainBuilder = new StringBuilder((int)domainSize);
        try
        {
            if (!NativeMethods.LookupAccountName(null, accountName, sidPtr, ref sidSize, domainBuilder, ref domainSize, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var systemName = new NativeMethods.LSA_UNICODE_STRING();
            var objectAttrs = new NativeMethods.LSA_OBJECT_ATTRIBUTES
            {
                Length = (uint)Marshal.SizeOf<NativeMethods.LSA_OBJECT_ATTRIBUTES>()
            };
            var status = NativeMethods.LsaOpenPolicy(ref systemName, ref objectAttrs, NativeMethods.POLICY_ALL_ACCESS, out var policyHandle);
            if (status != 0)
                throw new System.ComponentModel.Win32Exception(NativeMethods.LsaNtStatusToWinError(status));

            var rights = new[] { NativeMethods.CreateLsaString("SeServiceLogonRight") };
            try
            {
                status = NativeMethods.LsaAddAccountRights(policyHandle, sidPtr, rights, 1);
                if (status != 0)
                    throw new System.ComponentModel.Win32Exception(NativeMethods.LsaNtStatusToWinError(status));

                Log($"Granted 'Log on as a service' right to '{accountName}'.");
            }
            finally
            {
                NativeMethods.LsaClose(policyHandle);
                Marshal.FreeHGlobal(rights[0].Buffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sidPtr);
        }
    }

    private ServiceConfig? QueryServiceConfigWin32()
    {
        var hSCManager = IntPtr.Zero;
        var hService = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            hSCManager = NativeMethods.OpenSCManager(null, null, NativeMethods.SERVICE_QUERY_CONFIG);
            if (hSCManager == IntPtr.Zero)
            {
                Log($"OpenSCManager failed: {new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}");
                return null;
            }

            hService = NativeMethods.OpenService(hSCManager, ServiceName, NativeMethods.SERVICE_QUERY_CONFIG);
            if (hService == IntPtr.Zero)
                return null; // Service does not exist

            // First call to get required buffer size
            NativeMethods.QueryServiceConfig(hService, IntPtr.Zero, 0, out var bytesNeeded);
            var lastError = (uint)Marshal.GetLastWin32Error();
            if (lastError != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
            {
                Log($"QueryServiceConfig sizing call failed with error {lastError}");
                return null;
            }

            buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            if (!NativeMethods.QueryServiceConfig(hService, buffer, bytesNeeded, out _))
            {
                Log($"QueryServiceConfig failed: {new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}");
                return null;
            }

            var config = Marshal.PtrToStructure<NativeMethods.QUERY_SERVICE_CONFIG>(buffer);
            var binPath = config.lpBinaryPathName != IntPtr.Zero
                ? Marshal.PtrToStringUni(config.lpBinaryPathName) ?? "UNKNOWN"
                : "UNKNOWN";
            var account = config.lpServiceStartName != IntPtr.Zero
                ? Marshal.PtrToStringUni(config.lpServiceStartName) ?? "UNKNOWN"
                : "UNKNOWN";

            // Map dwStartType to a human-readable string compatible with existing comparison logic
            var startTypeStr = config.dwStartType switch
            {
                0 => "BOOT_START",
                1 => "SYSTEM_START",
                2 => "AUTO_START",
                3 => "DEMAND_START",
                4 => "DISABLED",
                _ => $"UNKNOWN({config.dwStartType})"
            };

            // Check for delayed auto-start via registry
            if (config.dwStartType == NativeMethods.SERVICE_AUTO_START)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
                    var delayedValue = key?.GetValue("DelayedAutostart");
                    if (delayedValue is int delayed && delayed == 1)
                        startTypeStr = "AUTO_START (DELAYED)";
                }
                catch
                {
                    // Registry read is best-effort
                }
            }

            return new ServiceConfig(binPath, startTypeStr, account);
        }
        catch (Exception ex)
        {
            Log($"Failed to query service config: {ex.Message}");
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (hService != IntPtr.Zero) NativeMethods.CloseServiceHandle(hService);
            if (hSCManager != IntPtr.Zero) NativeMethods.CloseServiceHandle(hSCManager);
        }
    }

    private record ServiceConfig(string BinPath, string StartType, string ServiceAccount);

    private async Task StartServiceAsync()
    {
        using var sc = new ServiceController(ServiceName);
        sc.Start();
        Log($"Service start requested, waiting for Running status...");

        await Task.Run(() =>
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(120)));

        Log($"Service is running (status: {sc.Status}).");
    }

    /// <summary>
    /// Callback invoked when TLS validation fails. The window wires this up to show
    /// the certificate to the user and ask for confirmation. Returns true to accept.
    /// </summary>
    public Func<X509Certificate2, Task<bool>>? ConfirmUntrustedCertificate { get; set; }

    /// <summary>
    /// Callback invoked when the user clicks Cancel during or after a partial install.
    /// Receives a description of what has been done. Returns true to confirm exit.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmCancelInstallation { get; set; }

    /// <summary>
    /// Callback invoked when an existing service has different configuration than what the
    /// installer would set. Receives a description of the differences. Returns true to reconfigure.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmServiceReconfigure { get; set; }

    /// <summary>
    /// Callback invoked to ask Hannah's permission before modifying the certificate's private
    /// key security to grant the service account read access.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmCertificateKeyPermission { get; set; }

    private async Task GrantCertificatePrivateKeyAccessAsync()
    {
        if (SelectedCertificate == null) return;

        var thumbprint = SelectedCertificate.Thumbprint;
        Log($"Checking private key access for certificate {thumbprint}");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        store.Close();

        if (certs.Count == 0)
        {
            Log("Certificate not found in store — skipping key permission grant.");
            return;
        }

        var cert = certs[0];
        string? keyFilePath = null;
        string? providerName = null;

        try
        {
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is RSACng rsaCng)
            {
                providerName = rsaCng.Key.Provider?.Provider;
                var uniqueName = rsaCng.Key.UniqueName;
                Log($"RSA CNG key — provider: {providerName ?? "(null)"}, uniqueName: {uniqueName ?? "(null)"}");
                keyFilePath = FindPrivateKeyFile(uniqueName);
            }
            else if (rsa is RSACryptoServiceProvider rsaCsp)
            {
                providerName = rsaCsp.CspKeyContainerInfo.ProviderName;
                var uniqueName = rsaCsp.CspKeyContainerInfo.UniqueKeyContainerName;
                Log($"RSA CSP key — provider: {providerName ?? "(null)"}, uniqueName: {uniqueName ?? "(null)"}");
                keyFilePath = FindPrivateKeyFile(uniqueName);
            }
        }
        catch (CryptographicException ex)
        {
            Log($"Cannot access private key (provider may be TPM-based): {ex.Message}");
        }

        // Also try ECDsa keys
        if (keyFilePath == null)
        {
            try
            {
                using var ecdsa = cert.GetECDsaPrivateKey();
                if (ecdsa is ECDsaCng ecdsaCng)
                {
                    providerName = ecdsaCng.Key.Provider?.Provider;
                    var uniqueName = ecdsaCng.Key.UniqueName;
                    Log($"ECDsa CNG key — provider: {providerName ?? "(null)"}, uniqueName: {uniqueName ?? "(null)"}");
                    keyFilePath = FindPrivateKeyFile(uniqueName);
                }
            }
            catch (CryptographicException)
            {
                /* not an ECDsa cert, or key inaccessible */
            }
        }

        if (keyFilePath == null)
        {
            var reason = providerName != null && providerName.Contains("Platform", StringComparison.OrdinalIgnoreCase)
                ? $"TPM-backed key ({providerName}) — cannot grant file ACL access."
                : "Private key file not found — cannot grant access.";

            Log($"Skipping private key ACL grant: {reason}");
            return;
        }

        Log($"Private key file: {keyFilePath} (provider: {providerName})");

        // Ask permission before modifying the key's security
        var message = $"The service account '{ServiceAccount}' needs read access to the TLS certificate's "
            + "private key to perform HTTPS connections.\n\n"
            + $"Certificate: {SelectedCertificate.DisplayText}\n"
            + $"Key file: {keyFilePath}\n\n"
            + "Grant the service account read access to this private key?";

        if (ConfirmCertificateKeyPermission != null)
        {
            var approved = await ConfirmCertificateKeyPermission(message);
            if (!approved)
            {
                Log("User declined to grant private key access.");
                throw new InvalidOperationException(
                    "Private key access was not granted. The service will not be able to serve HTTPS.\n\n"
                    + "You can manually grant access later using the Certificates MMC snap-in:\n"
                    + "  Right-click certificate → All Tasks → Manage Private Keys\n"
                    + $"  Grant '{ServiceAccount}' read access.");
            }
        }

        // Grant read access to the service account
        var fileInfo = new FileInfo(keyFilePath);
        var acl = fileInfo.GetAccessControl();

        var serviceIdentity = new NTAccount(ServiceAccount);
        acl.AddAccessRule(new FileSystemAccessRule(
            serviceIdentity, FileSystemRights.Read, AccessControlType.Allow));
        fileInfo.SetAccessControl(acl);

        Log($"Granted '{ServiceAccount}' read access to private key file: {keyFilePath}");
        _completedActions.Add($"Granted '{ServiceAccount}' read access to TLS certificate private key");
    }

    /// <summary>
    /// Searches all known private key directories for a key file with the given unique name.
    /// .NET may present legacy CSP keys (e.g. SChannel provider) as RSACng, so we search
    /// both CNG and legacy CSP directories regardless of the runtime type.
    /// </summary>
    private string? FindPrivateKeyFile(string? uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName))
            return null;

        // Some implementations return a full path as the UniqueName
        if (Path.IsPathRooted(uniqueName) && File.Exists(uniqueName))
            return uniqueName;

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        string[] candidateDirs =
        [
            Path.Combine(commonAppData, "Microsoft", "Crypto", "RSA", "MachineKeys"),
            Path.Combine(commonAppData, "Microsoft", "Crypto", "Keys"),
            Path.Combine(commonAppData, "Microsoft", "Crypto", "SystemKeys"),
        ];

        foreach (var dir in candidateDirs)
        {
            var candidate = Path.Combine(dir, uniqueName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private async Task CreateAdminUserAsync()
    {
        var scheme = UseTls ? "https" : "http";
        var baseUrl = $"{scheme}://localhost:{Port}";

        string? acceptedThumbprint = null;

        if (UseTls)
        {
            // If Hannah already trusted the self-signed cert at selection time, skip the probe
            if (TrustSelfSignedCert && SelectedCertificate != null)
            {
                Log("Self-signed certificate pre-trusted — skipping TLS probe dialog.");
                acceptedThumbprint = SelectedCertificate.Thumbprint;
            }
            else
            {
            // Phase 1: try with standard validation — capture cert on failure
            X509Certificate2? capturedCert = null;

            using var probeHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                {
                    if (errors == System.Net.Security.SslPolicyErrors.None)
                        return true;

                    if (cert != null)
                        capturedCert = new X509Certificate2(cert);
                    return false;
                }
            };

            using var probeClient = new HttpClient(probeHandler)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };

            try
            {
                await probeClient.GetAsync("/api/auth/setup");
                // If we get here, cert was trusted — proceed normally
            }
            catch (HttpRequestException) when (capturedCert != null)
            {
                // Cert was not trusted — ask the user
                if (ConfirmUntrustedCertificate != null)
                {
                    var accepted = await ConfirmUntrustedCertificate(capturedCert);
                    if (!accepted)
                        throw new InvalidOperationException(
                            "Certificate was not trusted. Admin user was not created. " +
                            "You can create one manually after configuring a trusted certificate.");

                    acceptedThumbprint = capturedCert.Thumbprint;
                }
                else
                {
                    throw;
                }
            }
            catch (HttpRequestException ex) when (capturedCert == null)
            {
                Log($"TLS probe failed without receiving a server certificate: {ex.Message}");
                throw new InvalidOperationException(
                    "The service is running but the TLS handshake failed before any certificate "
                    + "was received. This usually means the service account cannot access the "
                    + "certificate's private key.\n\n"
                    + $"Service account: {ServiceAccount}\n"
                    + $"Certificate: {SelectedCertificate?.DisplayText ?? "(unknown)"}\n\n"
                    + "To fix this:\n"
                    + "  • Re-run the installer with a certificate that has a software-based private key\n"
                    + "  • Or grant the service account access via Certificates MMC snap-in:\n"
                    + "    Right-click certificate → All Tasks → Manage Private Keys", ex);
            }
            } // end else (not pre-trusted)
        }

        // Phase 2: actual admin user creation (with pinned thumbprint if needed)
        using var handler = new HttpClientHandler();
        if (acceptedThumbprint != null)
        {
            var pinned = acceptedThumbprint;
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert != null && string.Equals(cert.GetCertHashString(), pinned, StringComparison.OrdinalIgnoreCase);
        }

        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };

        var payload = new { username = AdminUsername, password = AdminPassword };
        var json = JsonSerializer.Serialize(payload);

        // Retry a few times — service may still be starting
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
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

    private void LoadStoreCertificates()
    {
        StoreCertificates.Clear();
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

            var now = DateTime.Now;
            foreach (var cert in store.Certificates
                .OfType<X509Certificate2>()
                .Where(c => c.NotAfter > now && c.HasPrivateKey)
                .OrderBy(c => c.GetNameInfo(X509NameType.SimpleName, false)))
            {
                var simpleName = cert.GetNameInfo(X509NameType.SimpleName, false);
                var dnsNames = cert.GetNameInfo(X509NameType.DnsName, false);
                var expiry = cert.NotAfter.ToString("yyyy-MM-dd");
                var thumbprint = cert.Thumbprint;
                var issuer = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
                var serialNumber = cert.SerialNumber;

                // Show DNS name if different from simple name, otherwise just simple name
                var primaryName = !string.IsNullOrEmpty(dnsNames) && dnsNames != simpleName
                    ? dnsNames
                    : simpleName;

                StoreCertificates.Add(new CertificateEntry(
                    Subject: simpleName,
                    DnsName: dnsNames ?? string.Empty,
                    Thumbprint: thumbprint,
                    Expiry: expiry,
                    Issuer: issuer ?? string.Empty,
                    SerialNumber: serialNumber ?? string.Empty,
                    DisplayText: $"{primaryName}  (expires {expiry}, issued by {issuer})"));
            }

            store.Close();
        }
        catch
        {
            // May fail if not running elevated or store is empty — that's OK
        }
    }

    private void ValidateSelectedCertificateKey()
    {
        CertificateWarning = string.Empty;
        IsSelfSignedCert = false;
        TrustSelfSignedCert = false;

        if (_selectedCertificate == null) return;

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var certs = store.Certificates.Find(
                X509FindType.FindByThumbprint, _selectedCertificate.Thumbprint, validOnly: false);
            store.Close();

            if (certs.Count == 0) return;

            var cert = certs[0];

            // Detect self-signed: issuer matches subject
            IsSelfSignedCert = string.Equals(
                cert.Issuer, cert.Subject, StringComparison.OrdinalIgnoreCase);
            if (IsSelfSignedCert)
                Log($"Certificate {_selectedCertificate.Thumbprint} is self-signed.");

            string? providerName = null;

            try
            {
                using var rsa = cert.GetRSAPrivateKey();
                if (rsa is RSACng rsaCng)
                    providerName = rsaCng.Key.Provider?.Provider;
            }
            catch (CryptographicException)
            {
                // Key inaccessible — likely TPM
            }

            if (providerName == null)
            {
                try
                {
                    using var ecdsa = cert.GetECDsaPrivateKey();
                    if (ecdsa is ECDsaCng ecdsaCng)
                        providerName = ecdsaCng.Key.Provider?.Provider;
                }
                catch (CryptographicException)
                {
                    /* not ECDsa or inaccessible */
                }
            }

            if (providerName != null && providerName.Contains("Platform", StringComparison.OrdinalIgnoreCase))
            {
                CertificateWarning = $"⚠ This certificate uses a TPM-backed key ({providerName}). "
                    + $"The service account '{ServiceAccount}' will not be able to use it for TLS. "
                    + "Please select a certificate with a software-based private key instead.";
                Log($"Certificate {_selectedCertificate.Thumbprint} uses TPM provider: {providerName}");
            }
            else if (providerName == null)
            {
                CertificateWarning = "⚠ Could not access this certificate's private key. "
                    + $"The service account '{ServiceAccount}' may not be able to use it for TLS.";
                Log($"Certificate {_selectedCertificate.Thumbprint}: private key inaccessible");
            }
        }
        catch (Exception ex)
        {
            Log($"Certificate key validation failed: {ex.Message}");
        }
    }

    private void ViewCertificateDetails()
    {
        if (SelectedCertificate == null) return;

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var certs = store.Certificates.Find(
                X509FindType.FindByThumbprint, SelectedCertificate.Thumbprint, validOnly: false);
            store.Close();

            if (certs.Count > 0)
            {
                NativeMethods.ShowCertificateDialog(certs[0]);
            }
        }
        catch
        {
            // Non-critical — UI display only
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CRYPTUI_VIEWCERTIFICATE_STRUCT
        {
            public int dwSize;
            public IntPtr hwndParent;
            public int dwFlags;
            [MarshalAs(UnmanagedType.LPWStr)] public string? szTitle;
            public IntPtr pCertContext;
            public IntPtr rgszPurposes;
            public int cPurposes;
            public IntPtr pCryptProviderData;
            public int fpCryptProviderDataTrustedUsage;
            public int idxSigner;
            public int idxCert;
            public int fCounterSigner;
            public int idxCounterSigner;
            public int cStores;
            public IntPtr rghStores;
            public int cPropSheetPages;
            public IntPtr rgPropSheetPages;
            public int nStartPage;
        }

        [DllImport("cryptui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptUIDlgViewCertificateW(
            ref CRYPTUI_VIEWCERTIFICATE_STRUCT pCertViewInfo,
            out bool pfPropertiesChanged);

        public static void ShowCertificateDialog(X509Certificate2 certificate)
        {
            var viewInfo = new CRYPTUI_VIEWCERTIFICATE_STRUCT
            {
                dwSize = Marshal.SizeOf<CRYPTUI_VIEWCERTIFICATE_STRUCT>(),
                pCertContext = certificate.Handle,
                szTitle = "Certificate Details"
            };

            CryptUIDlgViewCertificateW(ref viewInfo, out _);
        }

        // Service Control Manager
        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateService(
            IntPtr hSCManager, string serviceName, string displayName,
            uint desiredAccess, uint serviceType, uint startType, uint errorControl,
            string binaryPathName, string? loadOrderGroup, IntPtr tagId,
            string? dependencies, string? serviceStartName, string? password);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenService(IntPtr hSCManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig(
            IntPtr hService, uint serviceType, uint startType, uint errorControl,
            string? binaryPathName, string? loadOrderGroup, IntPtr tagId,
            string? dependencies, string? serviceStartName, string? password,
            string? displayName);

        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig2(IntPtr hService, uint infoLevel, ref SERVICE_DESCRIPTION info);

        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig2(IntPtr hService, uint infoLevel, ref SERVICE_FAILURE_ACTIONS info);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceConfig(IntPtr hService, IntPtr serviceConfig, uint bufSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr hSCObject);

        // LSA for "Log on as a service" right
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern uint LsaOpenPolicy(
            ref LSA_UNICODE_STRING systemName, ref LSA_OBJECT_ATTRIBUTES objectAttributes,
            uint desiredAccess, out IntPtr policyHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern uint LsaAddAccountRights(
            IntPtr policyHandle, IntPtr accountSid,
            LSA_UNICODE_STRING[] userRights, uint countOfRights);

        [DllImport("advapi32.dll")]
        public static extern uint LsaClose(IntPtr objectHandle);

        [DllImport("advapi32.dll")]
        public static extern int LsaNtStatusToWinError(uint status);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupAccountName(
            string? systemName, string accountName,
            IntPtr sid, ref uint sidSize,
            StringBuilder? domainName, ref uint domainSize,
            out int accountType);

        // Constants
        public const uint SC_MANAGER_ALL_ACCESS  = 0xF003F;
        public const uint SERVICE_ALL_ACCESS     = 0xF01FF;
        public const uint SERVICE_QUERY_CONFIG   = 0x0001;
        public const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        public const uint SERVICE_AUTO_START     = 0x00000002;
        public const uint SERVICE_ERROR_NORMAL   = 0x00000001;
        public const uint SERVICE_NO_CHANGE      = 0xFFFFFFFF;
        public const uint SERVICE_CONFIG_DESCRIPTION      = 1;
        public const uint SERVICE_CONFIG_FAILURE_ACTIONS   = 2;
        public const uint POLICY_ALL_ACCESS      = 0x00F0FFF;
        public const uint DELETE                 = 0x00010000;
        public const uint ERROR_INSUFFICIENT_BUFFER = 122;
        public const uint SC_ACTION_RESTART      = 1;

        // Structs
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SERVICE_DESCRIPTION
        {
            public string lpDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_FAILURE_ACTIONS
        {
            public uint dwResetPeriod;
            public IntPtr lpRebootMsg;
            public IntPtr lpCommand;
            public uint cActions;
            public IntPtr lpsaActions;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SC_ACTION
        {
            public uint Type;
            public uint Delay;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LSA_UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LSA_OBJECT_ATTRIBUTES
        {
            public uint Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct QUERY_SERVICE_CONFIG
        {
            public uint dwServiceType;
            public uint dwStartType;
            public uint dwErrorControl;
            public IntPtr lpBinaryPathName;
            public IntPtr lpLoadOrderGroup;
            public uint dwTagId;
            public IntPtr lpDependencies;
            public IntPtr lpServiceStartName;
            public IntPtr lpDisplayName;
        }

        public static LSA_UNICODE_STRING CreateLsaString(string value)
        {
            var s = new LSA_UNICODE_STRING
            {
                Length = (ushort)(value.Length * sizeof(char)),
                MaximumLength = (ushort)((value.Length + 1) * sizeof(char)),
                Buffer = Marshal.StringToHGlobalUni(value)
            };
            return s;
        }
    }

    private void GenerateGrantScript()
    {
        try
        {
            var scriptPath = Path.Combine(InstallPath, "grant-permissions.sql");
            var account = ServiceAccount;

            var script = $"""
                /* ============================================================
                   SqlAgMonitor — Required SQL Server Permissions
                   ============================================================
                   Run this script on EACH SQL Server instance that hosts an
                   Availability Group or Distributed Availability Group you
                   want to monitor.

                   Service account: {account}
                   Generated by SqlAgMonitor Installer v{AppVersion}
                   ============================================================ */

                USE [master];

                /* Create a server login for the service account if it doesn't already exist */
                IF NOT EXISTS
                (
                    SELECT
                        1
                    FROM
                        sys.server_principals
                    WHERE
                        [name] = N'{account.Replace("'", "''")}'
                )
                BEGIN
                    CREATE LOGIN [{account}] FROM WINDOWS
                        WITH DEFAULT_DATABASE = [master];
                END;

                /* VIEW SERVER STATE — required for sys.dm_hadr_* DMVs */
                GRANT VIEW SERVER STATE TO [{account}];

                /* VIEW ANY DEFINITION — required for sys.availability_groups
                   and sys.availability_replicas catalog views */
                GRANT VIEW ANY DEFINITION TO [{account}];

                PRINT 'Permissions granted to [{account}] successfully.';
                """;

            File.WriteAllText(scriptPath, script);
            GrantScriptPath = scriptPath;
            this.RaisePropertyChanged(nameof(HasGrantScript));
            Log($"Generated grant script at {scriptPath}");
        }
        catch (Exception ex)
        {
            Log($"Failed to generate grant script: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            File.AppendAllText(LogPath, line);
        }
        catch
        {
            /* logging is best-effort */
        }
    }
}

public record CertificateEntry(
    string Subject, string DnsName, string Thumbprint,
    string Expiry, string Issuer, string SerialNumber, string DisplayText)
{
    public override string ToString() => DisplayText;
}
