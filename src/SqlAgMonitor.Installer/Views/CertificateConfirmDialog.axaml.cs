using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SqlAgMonitor.Installer.Views;

public partial class CertificateConfirmDialog : Window
{
    private readonly X509Certificate2 _certificate;
    private bool _accepted;

    public CertificateConfirmDialog() : this(null!)
    {
    }

    public CertificateConfirmDialog(X509Certificate2 certificate)
    {
        _certificate = certificate;
        InitializeComponent();

        var subjectText = this.FindControl<TextBlock>("SubjectText")!;
        var issuerText = this.FindControl<TextBlock>("IssuerText")!;
        var expiryText = this.FindControl<TextBlock>("ExpiryText")!;
        var thumbprintText = this.FindControl<TextBlock>("ThumbprintText")!;

        subjectText.Text = $"Subject: {certificate.Subject}";
        issuerText.Text = $"Issuer: {certificate.Issuer}";
        expiryText.Text = $"Valid: {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}";
        thumbprintText.Text = $"Thumbprint: {certificate.Thumbprint}";

        var viewBtn = this.FindControl<Button>("ViewCertButton")!;
        var acceptBtn = this.FindControl<Button>("AcceptButton")!;
        var rejectBtn = this.FindControl<Button>("RejectButton")!;

        viewBtn.Click += OnViewCertificate;
        acceptBtn.Click += OnAccept;
        rejectBtn.Click += OnReject;
    }

    public bool Accepted => _accepted;

    private void OnViewCertificate(object? sender, RoutedEventArgs e)
    {
        try
        {
            ShowCertificateDialog(_certificate);
        }
        catch
        {
            // Non-critical
        }
    }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close();
    }

    private void OnReject(object? sender, RoutedEventArgs e)
    {
        _accepted = false;
        Close();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CRYPTUI_VIEWCERTIFICATE_STRUCT
    {
        public int dwSize;
        public IntPtr hwndParent;
        public int dwFlags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? szTitle;
        public IntPtr pCertContext;
        public IntPtr rgszPurposes;
        public int cPurposes;
        public IntPtr pCryptProviderData;
        public int fpCryptProviderDataTrustedUsage;
        public int idxSigner;
        public int idxCert;
        public int fCounterSigner;
        public int idxCounterSigner;
        public int cStores;
        public IntPtr rghStores;
        public int cPropSheetPages;
        public IntPtr rgPropSheetPages;
        public int nStartPage;
    }

    [DllImport("cryptui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUIDlgViewCertificateW(
        ref CRYPTUI_VIEWCERTIFICATE_STRUCT pCertViewInfo,
        out bool pfPropertiesChanged);

    private static void ShowCertificateDialog(X509Certificate2 certificate)
    {
        var viewInfo = new CRYPTUI_VIEWCERTIFICATE_STRUCT
        {
            dwSize = Marshal.SizeOf<CRYPTUI_VIEWCERTIFICATE_STRUCT>(),
            pCertContext = certificate.Handle,
            szTitle = "Certificate Details"
        };

        CryptUIDlgViewCertificateW(ref viewInfo, out _);
    }
}
