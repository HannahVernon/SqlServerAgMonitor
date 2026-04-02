using Avalonia.ReactiveUI;
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
        }
    }
}
