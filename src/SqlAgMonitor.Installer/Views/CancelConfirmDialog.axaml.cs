using Avalonia.Controls;

namespace SqlAgMonitor.Installer.Views;

public partial class CancelConfirmDialog : Window
{
    private bool _confirmed;

    public CancelConfirmDialog() : this(string.Empty)
    {
    }

    public CancelConfirmDialog(string message) : this(
        "Cancel Installation?", "⚠ Cancel Installation?",
        message, "Yes, Cancel", "No, Go Back")
    {
    }

    public CancelConfirmDialog(
        string title, string heading, string message,
        string confirmLabel, string cancelLabel)
    {
        InitializeComponent();

        Title = title;

        var headingBlock = this.FindControl<TextBlock>("HeadingText")!;
        headingBlock.Text = heading;

        var messageText = this.FindControl<TextBlock>("MessageText")!;
        messageText.Text = message;

        var confirmBtn = this.FindControl<Button>("ConfirmButton")!;
        confirmBtn.Content = confirmLabel;
        confirmBtn.Click += (_, _) => { _confirmed = true; Close(); };

        var goBackBtn = this.FindControl<Button>("GoBackButton")!;
        goBackBtn.Content = cancelLabel;
        goBackBtn.Click += (_, _) => { _confirmed = false; Close(); };
    }

    public bool Confirmed => _confirmed;
}
