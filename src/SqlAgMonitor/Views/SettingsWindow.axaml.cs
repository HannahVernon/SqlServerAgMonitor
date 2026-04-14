using System.Collections.Generic;
using System.Linq;
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

                vm.CloseRequested = async saved =>
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

                        /* Store the SMTP password securely via credential store */
                        var smtpCredStore = App.Services.GetRequiredService<ICredentialStore>();
                        if (!string.IsNullOrEmpty(vm.EmailPassword))
                        {
                            const string smtpCredentialKey = "smtp-password";
                            config.Email.CredentialKey = smtpCredentialKey;
                            await smtpCredStore.StorePasswordAsync(smtpCredentialKey, vm.EmailPassword);
                        }
                        else if (string.IsNullOrEmpty(vm.EmailUsername))
                        {
                            /* No username and no new password — clear the credential */
                            if (!string.IsNullOrEmpty(config.Email.CredentialKey))
                            {
                                await smtpCredStore.DeletePasswordAsync(config.Email.CredentialKey);
                            }
                            config.Email.CredentialKey = null;
                        }

                        configService.Save(config);

                        var themeService = App.Services.GetRequiredService<IThemeService>();
                        themeService.SetTheme(config.Theme);

                        /* Offer config migration if service mode was just enabled */
                        if (vm.ShouldOfferMigration && config.MonitoredGroups.Count > 0)
                        {
                            await OfferMigrationAsync(vm, config);
                        }
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

    private async Task OfferMigrationAsync(SettingsViewModel vm, AppConfiguration config)
    {
        var localGroupNames = config.MonitoredGroups.Select(g => g.Name).ToList();

        var sqlAuthGroupNames = config.MonitoredGroups
            .Where(g => g.Connections.Any(c =>
                string.Equals(c.AuthType, "sql", System.StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.CredentialKey)))
            .Select(g => g.Name)
            .ToList();

        var serviceGroupNames = await vm.FetchServiceGroupNamesAsync();

        var dialog = new MigrationDialog(localGroupNames, serviceGroupNames, sqlAuthGroupNames, vm.MigrateSelectedGroupsAsync);
        await dialog.ShowDialog(this);
    }
}
