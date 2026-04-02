using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using SqlAgMonitor.Installer.ViewModels;

namespace SqlAgMonitor.Installer.Views;

public partial class InstallerWindow : ReactiveWindow<InstallerViewModel>
{
    public InstallerWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is InstallerViewModel vm)
        {
            vm.CloseRequested += () => Close();
            vm.ConfirmUntrustedCertificate = PromptUserForCertificateTrust;
            vm.ConfirmCancelInstallation = PromptUserForCancelConfirmation;
            vm.ConfirmServiceReconfigure = PromptUserForServiceReconfigure;
            vm.ConfirmCertificateKeyPermission = PromptUserForCertKeyPermission;
        }
    }

    private async Task<bool> PromptUserForCertificateTrust(X509Certificate2 certificate)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new CertificateConfirmDialog(certificate);
            await dialog.ShowDialog(this);
            return dialog.Accepted;
        });
    }

    private async Task<bool> PromptUserForCancelConfirmation(string message)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new CancelConfirmDialog(message);
            await dialog.ShowDialog(this);
            return dialog.Confirmed;
        });
    }

    private async Task<bool> PromptUserForServiceReconfigure(string message)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new CancelConfirmDialog(
                "Service Configuration",
                "⚙ Existing Service Detected",
                message,
                "Yes, Reconfigure",
                "No, Keep Existing");
            await dialog.ShowDialog(this);
            return dialog.Confirmed;
        });
    }

    private async Task<bool> PromptUserForCertKeyPermission(string message)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new CancelConfirmDialog(
                "Certificate Private Key Access",
                "🔑 Private Key Permission Required",
                message,
                "Yes, Grant Access",
                "No, Skip");
            await dialog.ShowDialog(this);
            return dialog.Confirmed;
        });
    }
}
