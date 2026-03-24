using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SqlAgMonitor.Core.Services.Credentials;

public class AesCredentialStore : ICredentialStore
{
    private readonly ILogger<AesCredentialStore> _logger;
    private readonly IPasswordStrengthValidator _passwordValidator;
    private readonly string _storePath;
    private readonly object _lock = new();
    private byte[]? _derivedKey;

    private const int SaltSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 600_000;

    public bool IsUnlocked => _derivedKey != null;

    public AesCredentialStore(
        ILogger<AesCredentialStore> logger,
        IPasswordStrengthValidator passwordValidator,
        string? storeDirectory = null)
    {
        _logger = logger;
        _passwordValidator = passwordValidator;
        var dir = storeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlAgMonitor", "credentials");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "credentials.aes");
    }

    public void Unlock(string masterPassword)
    {
        var result = _passwordValidator.Validate(masterPassword);
        if (!result.IsValid)
            throw new ArgumentException($"Master password does not meet strength requirements: {result.Feedback}");

        _derivedKey = DeriveKey(masterPassword, GetOrCreateSalt());
        _logger.LogInformation("Credential store unlocked.");
    }

    public void Lock()
    {
        if (_derivedKey != null)
        {
            CryptographicOperations.ZeroMemory(_derivedKey);
            _derivedKey = null;
        }
        _logger.LogInformation("Credential store locked.");
    }

    public void SetMasterPassword(string masterPassword)
    {
        var result = _passwordValidator.Validate(masterPassword);
        if (!result.IsValid)
            throw new ArgumentException($"Master password does not meet strength requirements: {result.Feedback}");

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        SaveSalt(salt);
        _derivedKey = DeriveKey(masterPassword, salt);

        // Re-encrypt existing data if any
        if (File.Exists(_storePath))
        {
            var store = LoadStore();
            SaveStore(store);
        }

        _logger.LogInformation("Master password set for credential store.");
    }

    public Task<string?> GetPasswordAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        EnsureUnlocked();
        lock (_lock)
        {
            var store = LoadStore();
            return Task.FromResult(store.TryGetValue(credentialKey, out var password) ? password : null);
        }
    }

    public Task StorePasswordAsync(string credentialKey, string password, CancellationToken cancellationToken = default)
    {
        EnsureUnlocked();
        lock (_lock)
        {
            var store = LoadStore();
            store[credentialKey] = password;
            SaveStore(store);
            _logger.LogInformation("Credential stored for key {Key}.", credentialKey);
        }
        return Task.CompletedTask;
    }

    public Task DeletePasswordAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        EnsureUnlocked();
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
        EnsureUnlocked();
        lock (_lock)
        {
            var store = LoadStore();
            return Task.FromResult(store.ContainsKey(credentialKey));
        }
    }

    private void EnsureUnlocked()
    {
        if (_derivedKey == null)
            throw new InvalidOperationException("Credential store is locked. Call Unlock() with master password first.");
    }

    private Dictionary<string, string> LoadStore()
    {
        if (!File.Exists(_storePath))
            return new Dictionary<string, string>();

        try
        {
            var fileBytes = File.ReadAllBytes(_storePath);
            if (fileBytes.Length < NonceSize + TagSize + 1)
                return new Dictionary<string, string>();

            var nonce = fileBytes.AsSpan(0, NonceSize);
            var tag = fileBytes.AsSpan(fileBytes.Length - TagSize, TagSize);
            var ciphertext = fileBytes.AsSpan(NonceSize, fileBytes.Length - NonceSize - TagSize);

            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(_derivedKey!, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var json = Encoding.UTF8.GetString(plaintext);
            CryptographicOperations.ZeroMemory(plaintext);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt credential store. Wrong master password?");
            throw new InvalidOperationException("Failed to decrypt credential store. The master password may be incorrect.", ex);
        }
    }

    private void SaveStore(Dictionary<string, string> store)
    {
        var json = JsonSerializer.Serialize(store);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_derivedKey!, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        CryptographicOperations.ZeroMemory(plaintext);

        using var fs = File.Create(_storePath);
        fs.Write(nonce);
        fs.Write(ciphertext);
        fs.Write(tag);
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA512,
            32);
    }

    private byte[] GetOrCreateSalt()
    {
        var saltPath = Path.ChangeExtension(_storePath, ".salt");
        if (File.Exists(saltPath))
            return File.ReadAllBytes(saltPath);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        SaveSalt(salt);
        return salt;
    }

    private void SaveSalt(byte[] salt)
    {
        var saltPath = Path.ChangeExtension(_storePath, ".salt");
        File.WriteAllBytes(saltPath, salt);
    }
}
