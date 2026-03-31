namespace SqlAgMonitor.Services;

public interface IThemeService
{
    void SetTheme(string theme);
    string GetCurrentTheme();
}
