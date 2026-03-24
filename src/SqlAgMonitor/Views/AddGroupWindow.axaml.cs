using Avalonia.ReactiveUI;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class AddGroupWindow : ReactiveWindow<AddGroupViewModel>
{
    public AddGroupWindow()
    {
        InitializeComponent();
    }
}
