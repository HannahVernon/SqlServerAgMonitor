using Avalonia.ReactiveUI;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class SettingsWindow : ReactiveWindow<SettingsViewModel>
{
    public SettingsWindow()
    {
        InitializeComponent();
    }
}
