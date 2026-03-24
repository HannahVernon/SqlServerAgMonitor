using Avalonia;
using Avalonia.Styling;

namespace SqlAgMonitor.Services;

public class ThemeService
{
    public void SetTheme(string theme)
    {
        var app = Application.Current;
        if (app == null) return;

        app.RequestedThemeVariant = theme.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            "highcontrast" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }

    public string GetCurrentTheme()
    {
        var variant = Application.Current?.RequestedThemeVariant;
        if (variant == ThemeVariant.Light) return "light";
        if (variant == ThemeVariant.Dark) return "dark";
        return "dark";
    }
}
