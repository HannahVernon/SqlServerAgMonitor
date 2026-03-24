namespace SqlAgMonitor.Core.Configuration;

public interface IConfigurationService
{
    AppConfiguration Load();
    void Save(AppConfiguration config);
    string ConfigFilePath { get; }
}
