using System.Text.Json;

namespace SqlAgMonitor.Service.Auth;

/// <summary>
/// File-based local user store with bcrypt-hashed passwords.
/// Stored at %APPDATA%/SqlAgMonitor/service/users.json.
/// Creates a default admin account on first run.
/// </summary>
public sealed class UserStore
{
    private readonly string _storePath;
    private readonly ILogger<UserStore> _logger;
    private readonly object _lock = new();
    private Dictionary<string, UserRecord> _users = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UserStore(ILogger<UserStore> logger)
    {
        _logger = logger;

        var storeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlAgMonitor", "service");
        Directory.CreateDirectory(storeDirectory);
        _storePath = Path.Combine(storeDirectory, "users.json");

        Load();
    }

    /// <summary>Validates username and password. Returns true if credentials are correct.</summary>
    public bool ValidateCredentials(string username, string password)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(username, out var record))
                return false;

            return BCrypt.Net.BCrypt.Verify(password, record.PasswordHash);
        }
    }

    /// <summary>Checks whether any users exist in the store.</summary>
    public bool HasUsers()
    {
        lock (_lock)
        {
            return _users.Count > 0;
        }
    }

    /// <summary>Creates a new user. Returns false if user already exists.</summary>
    public bool CreateUser(string username, string password)
    {
        lock (_lock)
        {
            if (_users.ContainsKey(username))
                return false;

            _users[username] = new UserRecord
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
                CreatedUtc = DateTimeOffset.UtcNow
            };

            Save();
            _logger.LogInformation("Created user {Username}", username);
            return true;
        }
    }

    /// <summary>Changes a user's password. Returns false if user doesn't exist.</summary>
    public bool ChangePassword(string username, string newPassword)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(username, out var record))
                return false;

            record.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
            Save();
            _logger.LogInformation("Changed password for {Username}", username);
            return true;
        }
    }

    /// <summary>Returns the list of usernames.</summary>
    public IReadOnlyList<string> GetUsernames()
    {
        lock (_lock)
        {
            return _users.Keys.ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                _logger.LogInformation("No user store found at {Path} — starting empty", _storePath);
                return;
            }

            var json = File.ReadAllText(_storePath);
            var records = JsonSerializer.Deserialize<List<UserRecord>>(json, JsonOptions) ?? [];

            _users = records.ToDictionary(r => r.Username, r => r, StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation("Loaded {Count} user(s) from {Path}", _users.Count, _storePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load user store from {Path}", _storePath);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_users.Values.ToList(), JsonOptions);
            File.WriteAllText(_storePath, json);
            FileAccessHelper.RestrictToCurrentUser(_storePath, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save user store to {Path}", _storePath);
        }
    }

    private sealed class UserRecord
    {
        public string Username { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
    }
}
