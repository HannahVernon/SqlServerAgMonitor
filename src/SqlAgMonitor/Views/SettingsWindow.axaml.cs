using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.Credentials;
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
                vm.ConfirmUntrustedCertificate = OnConfirmUntrustedCertificateAsync;

                vm.CloseRequested += async saved =>
                {
                    if (saved)
                    {
                        var configService = App.Services.GetRequiredService<IConfigurationService>();
                        var config = configService.Load();
                        vm.ApplyTo(config);

                        /* Store the service password securely via credential store */
                        if (!string.IsNullOrEmpty(vm.ServicePassword))
                        {
                            const string serviceCredentialKey = "service-password";
                            config.Service.CredentialKey = serviceCredentialKey;
                            var credStore = App.Services.GetRequiredService<ICredentialStore>();
                            await credStore.StorePasswordAsync(serviceCredentialKey, vm.ServicePassword);
                        }

                        configService.Save(config);

                        var themeService = App.Services.GetRequiredService<IThemeService>();
                        themeService.SetTheme(config.Theme);
                    }

                    Close();
                };
            }
        };
    }

    private async Task<bool> OnConfirmUntrustedCertificateAsync(X509Certificate2 certificate)
    {
        var dialog = new CertificateTrustDialog(certificate);
        await dialog.ShowDialog(this);
        return dialog.Accepted;
    }
}
