using Avalonia.ReactiveUI;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class AddGroupWindow : ReactiveWindow<AddGroupViewModel>
{
    public AddGroupWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is AddGroupViewModel vm)
            {
                vm.CloseRequested += finished =>
                {
                    Close(finished);
                };
            }
        };
    }
}
