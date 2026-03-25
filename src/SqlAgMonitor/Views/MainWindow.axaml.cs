using System;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isExiting && DataContext is MainWindowViewModel vm)
        {
            _isExiting = true;
            vm.ExitCommand.Execute().Subscribe();
        }
        base.OnClosing(e);
    }
}