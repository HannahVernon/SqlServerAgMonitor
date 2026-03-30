using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SqlAgMonitor.Core.Services.Credentials;

[SupportedOSPlatform("windows")]
public class DpapiCredentialStore : ICredentialStore
{
    private readonly ILogger<DpapiCredentialStore> _logger;
    private readonly string _storePath;
    private readonly object _lock = new();

    public DpapiCredentialStore(ILogger<DpapiCredentialStore> logger, string? storeDirectory = null)
    {
        _logger = logger;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");

        var dir = storeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlAgMonitor", "credentials");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "credentials.dat");
    }

    public Task<string?> GetPasswordAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var store = LoadStore();
            if (store.TryGetValue(credentialKey, out var encryptedBase64))
            {
                try
                {
                    var encryptedBytes = Convert.FromBase64String(encryptedBase64);
                    var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                    return Task.FromResult<string?>(Encoding.UTF8.GetString(decryptedBytes));
                }
                catch (CryptographicException ex)
                {
                    _logger.LogError(ex, "Failed to decrypt credential for key {Key}.", credentialKey);
                    return Task.FromResult<string?>(null);
                }
            }
            return Task.FromResult<string?>(null);
        }
    }

    public Task StorePasswordAsync(string credentialKey, string password, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var store = LoadStore();
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            var encryptedBytes = ProtectedData.Protect(passwordBytes, null, DataProtectionScope.CurrentUser);
            store[credentialKey] = Convert.ToBase64String(encryptedBytes);
            SaveStore(store);
            _logger.LogInformation("Credential stored for key {Key}.", credentialKey);
        }
        return Task.CompletedTask;
    }

    public Task DeletePasswordAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var store = LoadStore();
            if (store.Remove(credentialKey))
            {
                SaveStore(store);
                _logger.LogInformation("Credential deleted for key {Key}.", credentialKey);
            }
        }
        return Task.CompletedTask;
    }

    public Task<bool> HasPasswordAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var store = LoadStore();
            return Task.FromResult(store.ContainsKey(credentialKey));
        }
    }

    private Dictionary<string, string> LoadStore()
    {
        if (!File.Exists(_storePath))
            return new Dictionary<string, string>();

        try
        {
            var json = File.ReadAllText(_storePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load credential store from {Path}.", _storePath);
            return new Dictionary<string, string>();
        }
    }

    private void SaveStore(Dictionary<string, string> store)
    {
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_storePath, json);
    }

    public void Dispose()
    {
        // DPAPI does not hold sensitive key material in memory; nothing to clear.
    }
}
