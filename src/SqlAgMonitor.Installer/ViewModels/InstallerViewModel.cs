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
            (step, installing) => step < 6 && !installing);

        var canGoBack = this.WhenAnyValue(
            x => x.CurrentStep, x => x.IsInstalling,
            (step, installing) => step > 0 && !installing);

        var canInstall = this.WhenAnyValue(
            x => x.CurrentStep, x => x.IsInstalling,
            x => x.AdminPassword, x => x.AdminPasswordConfirm,
            (step, installing, pw, confirm) =>
                step == 6 && !installing &&
                !string.IsNullOrWhiteSpace(pw) && pw.Length >= 8 && pw == confirm);

        NextCommand = ReactiveCommand.Create(OnNext, canGoNext);
        BackCommand = ReactiveCommand.Create(OnBack, canGoBack);
        InstallCommand = ReactiveCommand.CreateFromTask(OnInstallAsync, canInstall);
        CloseCommand = ReactiveCommand.CreateFromTask(OnCloseAsync);

        var canViewCert = this.WhenAnyValue(x => x.SelectedCertificate)
            .Select(cert => cert != null);
        ViewCertificateCommand = ReactiveCommand.Create(ViewCertificateDetails, canViewCert);
    }

    // Commands
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> ViewCertificateCommand { get; }

    public event Action? CloseRequested;

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
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, Math.Clamp(value, 1024, 65535)); }

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

    /// <summary>Selected certificate thumbprint — from store selection or empty if using .pfx file.</summary>
    public string SelectedThumbprint => SelectedCertificate?.Thumbprint ?? string.Empty;

    // Step 5
    public string AdminUsername { get => _adminUsername; set => this.RaiseAndSetIfChanged(ref _adminUsername, value); }
    public string AdminPassword { get => _adminPassword; set => this.RaiseAndSetIfChanged(ref _adminPassword, value); }
    public string AdminPasswordConfirm { get => _adminPasswordConfirm; set => this.RaiseAndSetIfChanged(ref _adminPasswordConfirm, value); }

    public string CloseButtonText => IsComplete ? "Close" : "Cancel";

    // Progress
    public string ProgressText { get => _progressText; set => this.RaiseAndSetIfChanged(ref _progressText, value); }
    public double ProgressValue { get => _progressValue; set => this.RaiseAndSetIfChanged(ref _progressValue, value); }
    public string ErrorMessage { get => _errorMessage; set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }

    private void OnNext() => CurrentStep++;
    private void OnBack() => CurrentStep--;

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
        Log($"InstallPath={InstallPath}, Port={Port}, UseTls={UseTls}, ServiceAccount={ServiceAccount}");

        try
        {
            await SetProgress("Checking for existing installation...", 0);
            await ValidateInstallPathAsync();

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

            await SetProgress("Starting service...", 60);
            await StartServiceAsync();
            _completedActions.Add("Started the Windows Service");
            Log("Service started");

            await SetProgress("Creating admin user...", 80);
            await CreateAdminUserAsync();
            _completedActions.Add($"Created admin user '{AdminUsername}'");
            Log($"Created admin user '{AdminUsername}'");

            await SetProgress("Registering in Add/Remove Programs...", 90);
            WriteUninstallRegistry();
            _completedActions.Add("Registered in Add/Remove Programs");
            Log("Wrote uninstall registry entry");

            await SetProgress("Installation complete!", 100);
            IsComplete = true;
            CurrentStep = 7;
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
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
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

    private async Task ValidateInstallPathAsync()
    {
        var existingConfig = await QueryServiceConfigAsync();
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
                + $"  • Uninstall the existing service first (sc.exe delete {ServiceName})");
        }
    }

    private async Task CreateServiceAsync()
    {
        var exePath = Path.Combine(InstallPath, "SqlAgMonitor.Service.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Service executable not found at {exePath}");

        var expectedBinPath = $"\"{exePath}\"";
        var existingConfig = await QueryServiceConfigAsync();

        if (existingConfig != null)
        {
            Log($"Existing service found: BinPath={existingConfig.BinPath}, Account={existingConfig.ServiceAccount}, StartType={existingConfig.StartType}");

            // Path mismatch is already blocked by ValidateInstallPathAsync — only check account and start type
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

            // User approved reconfiguration — delete and recreate
            Log($"Deleting service '{ServiceName}' for reconfiguration...");
            var deleteExitCode = await RunProcessAsync("sc.exe", $"delete {ServiceName}");
            Log($"sc.exe delete exited with code {deleteExitCode}");
            if (deleteExitCode == 0)
                await Task.Delay(2000);
        }

        var args = $"create {ServiceName} binPath= {expectedBinPath} DisplayName= \"{DisplayName}\" start= delayed-auto obj= \"{ServiceAccount}\"";

        if (!UseLocalService && !string.IsNullOrEmpty(ServicePassword))
            args += $" password= \"{ServicePassword}\"";

        Log($"Running: sc.exe {args}");
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

    private async Task<ServiceConfig?> QueryServiceConfigAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"qc {ServiceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process == null) return null;

            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0) return null;

            // Parse sc.exe qc output — lines like "BINARY_PATH_NAME : ...", "START_TYPE : ...", "SERVICE_START_NAME : ..."
            string? binPath = null, startType = null, account = null;
            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
                    binPath = trimmed.Split(':', 2).ElementAtOrDefault(1)?.Trim();
                else if (trimmed.StartsWith("START_TYPE", StringComparison.OrdinalIgnoreCase))
                    startType = trimmed.Split(':', 2).ElementAtOrDefault(1)?.Trim();
                else if (trimmed.StartsWith("SERVICE_START_NAME", StringComparison.OrdinalIgnoreCase))
                    account = trimmed.Split(':', 2).ElementAtOrDefault(1)?.Trim();
            }

            if (binPath == null) return null;

            return new ServiceConfig(binPath, startType ?? "UNKNOWN", account ?? "UNKNOWN");
        }
        catch (Exception ex)
        {
            Log($"Failed to query service config: {ex.Message}");
            return null;
        }
    }

    private record ServiceConfig(string BinPath, string StartType, string ServiceAccount);

    private async Task StartServiceAsync()
    {
        var exitCode = await RunProcessAsync("sc.exe", $"start {ServiceName}");
        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to start service (exit code {exitCode}).");

        // Wait for the service to be ready
        await Task.Delay(3000);
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
                if (!string.IsNullOrEmpty(uniqueName))
                {
                    // CNG Software Key Storage Provider → %ProgramData%\Microsoft\Crypto\Keys\
                    var keysPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Microsoft", "Crypto", "Keys", uniqueName);
                    if (File.Exists(keysPath))
                        keyFilePath = keysPath;
                }
            }
            else if (rsa is RSACryptoServiceProvider rsaCsp)
            {
                providerName = rsaCsp.CspKeyContainerInfo.ProviderName;
                var uniqueName = rsaCsp.CspKeyContainerInfo.UniqueKeyContainerName;
                if (!string.IsNullOrEmpty(uniqueName))
                {
                    var machineKeysPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Microsoft", "Crypto", "RSA", "MachineKeys", uniqueName);
                    if (File.Exists(machineKeysPath))
                        keyFilePath = machineKeysPath;
                }
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
                    if (!string.IsNullOrEmpty(uniqueName))
                    {
                        var keysPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            "Microsoft", "Crypto", "Keys", uniqueName);
                        if (File.Exists(keysPath))
                            keyFilePath = keysPath;
                    }
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

    private async Task CreateAdminUserAsync()
    {
        var scheme = UseTls ? "https" : "http";
        var baseUrl = $"{scheme}://localhost:{Port}";

        string? acceptedThumbprint = null;

        if (UseTls)
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
