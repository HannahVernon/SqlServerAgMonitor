namespace SqlAgMonitor.Core.Configuration;

public interface IConfigurationService
{
    AppConfiguration Load();
    void Save(AppConfiguration config);
    string ConfigFilePath { get; }

    /// <summary>Raised after configuration is saved.</summary>
    event Action<AppConfiguration>? ConfigurationChanged;
}
