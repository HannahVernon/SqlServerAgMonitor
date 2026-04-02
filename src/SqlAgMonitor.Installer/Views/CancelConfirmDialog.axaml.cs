using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SqlAgMonitor.Installer.Views;

public partial class CancelConfirmDialog : Window
{
    private bool _confirmed;

    public CancelConfirmDialog() : this(string.Empty)
    {
    }

    public CancelConfirmDialog(string message)
    {
        InitializeComponent();

        var messageText = this.FindControl<TextBlock>("MessageText")!;
        messageText.Text = message;

        var confirmBtn = this.FindControl<Button>("ConfirmButton")!;
        var goBackBtn = this.FindControl<Button>("GoBackButton")!;

        confirmBtn.Click += (_, _) => { _confirmed = true; Close(); };
        goBackBtn.Click += (_, _) => { _confirmed = false; Close(); };
    }

    public bool Confirmed => _confirmed;
}
