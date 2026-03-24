using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Services;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class SettingsWindow : ReactiveWindow<SettingsViewModel>
{
    public SettingsWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.CloseRequested += saved =>
                {
                    if (saved)
                    {
                        var configService = App.Services.GetRequiredService<IConfigurationService>();
                        var config = configService.Load();
                        vm.ApplyTo(config);
                        configService.Save(config);

                        var themeService = new ThemeService();
                        themeService.SetTheme(config.Theme);
                    }

                    Close();
                };
            }
        };
    }
}
