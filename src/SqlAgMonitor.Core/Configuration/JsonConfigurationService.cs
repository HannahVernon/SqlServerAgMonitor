using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Services;

namespace SqlAgMonitor.Core.Configuration;

public class JsonConfigurationService : IConfigurationService
{
    private readonly ILogger<JsonConfigurationService> _logger;
    private readonly string _configFilePath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string ConfigFilePath => _configFilePath;
    public event Action<AppConfiguration>? ConfigurationChanged;

    public JsonConfigurationService(ILogger<JsonConfigurationService> logger, string? configDirectory = null)
    {
        _logger = logger;
        var dir = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlAgMonitor");
        Directory.CreateDirectory(dir);
        _configFilePath = Path.Combine(dir, "config.json");
    }

    public AppConfiguration Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_configFilePath))
            {
                _logger.LogInformation("No configuration file found at {Path}. Using defaults.", _configFilePath);
                var defaults = new AppConfiguration();
                Save(defaults);
                return defaults;
            }

            try
            {
                var json = File.ReadAllText(_configFilePath);
                var config = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions);
                return config ?? new AppConfiguration();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load configuration from {Path}. Using defaults.", _configFilePath);
                return new AppConfiguration();
            }
        }
    }

    public void Save(AppConfiguration config)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, JsonOptions);
                var dir = Path.GetDirectoryName(_configFilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(_configFilePath, json);
                FileAccessHelper.RestrictToCurrentUser(_configFilePath, _logger);
                _logger.LogInformation("Configuration saved to {Path}.", _configFilePath);
                ConfigurationChanged?.Invoke(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save configuration to {Path}.", _configFilePath);
                throw;
            }
        }
    }
}
