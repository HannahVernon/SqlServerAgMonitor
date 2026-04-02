using System;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SqlAgMonitor.Views;

public partial class CertificateTrustDialog : Window
{
    public bool Accepted { get; private set; }

    public CertificateTrustDialog()
    {
        InitializeComponent();
    }

    public CertificateTrustDialog(X509Certificate2 certificate)
    {
        InitializeComponent();

        var subjectText = this.FindControl<TextBlock>("SubjectText")!;
        var issuerText = this.FindControl<TextBlock>("IssuerText")!;
        var expiryText = this.FindControl<TextBlock>("ExpiryText")!;
        var thumbprintText = this.FindControl<TextBlock>("ThumbprintText")!;

        subjectText.Text = $"Subject: {certificate.Subject}";
        issuerText.Text = $"Issuer: {certificate.Issuer}";
        expiryText.Text = $"Valid: {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}";
        thumbprintText.Text = $"Thumbprint: {certificate.Thumbprint}";

        var acceptBtn = this.FindControl<Button>("AcceptBtn")!;
        var cancelBtn = this.FindControl<Button>("CancelBtn")!;

        acceptBtn.Click += OnAccept;
        cancelBtn.Click += OnCancel;
    }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}
