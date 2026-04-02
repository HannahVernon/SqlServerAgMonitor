using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SqlAgMonitor.Views;

public partial class CertificateTrustDialog : Window
{
    private readonly X509Certificate2 _certificate;

    public bool Accepted { get; private set; }

    public CertificateTrustDialog()
    {
        InitializeComponent();
        _certificate = null!;
    }

    public CertificateTrustDialog(X509Certificate2 certificate)
    {
        InitializeComponent();
        _certificate = certificate;

        var subjectText = this.FindControl<TextBlock>("SubjectText")!;
        var issuerText = this.FindControl<TextBlock>("IssuerText")!;
        var expiryText = this.FindControl<TextBlock>("ExpiryText")!;
        var thumbprintText = this.FindControl<TextBlock>("ThumbprintText")!;

        subjectText.Text = $"Subject: {certificate.Subject}";
        issuerText.Text = $"Issuer: {certificate.Issuer}";
        expiryText.Text = $"Valid: {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}";
        thumbprintText.Text = $"Thumbprint: {certificate.Thumbprint}";

        var viewBtn = this.FindControl<Button>("ViewCertBtn")!;
        var acceptBtn = this.FindControl<Button>("AcceptBtn")!;
        var cancelBtn = this.FindControl<Button>("CancelBtn")!;

        viewBtn.Click += OnViewCertificate;
        acceptBtn.Click += OnAccept;
        cancelBtn.Click += OnCancel;

        /* "View Certificate" only works on Windows (native P/Invoke) */
        viewBtn.IsVisible = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    private void OnViewCertificate(object? sender, RoutedEventArgs e)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            var certInfo = new NativeMethods.CRYPTUI_VIEWCERTIFICATE_STRUCT
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.CRYPTUI_VIEWCERTIFICATE_STRUCT>(),
                pCertContext = _certificate.Handle,
                szTitle = "Server Certificate"
            };
            NativeMethods.CryptUIDlgViewCertificateW(ref certInfo, out _);
        }
        catch
        {
            /* Best-effort — if native call fails, just ignore */
        }
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

    private static class NativeMethods
    {
        [DllImport("cryptui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CryptUIDlgViewCertificateW(
            ref CRYPTUI_VIEWCERTIFICATE_STRUCT pCertViewInfo,
            out bool pfPropertiesChanged);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CRYPTUI_VIEWCERTIFICATE_STRUCT
        {
            public uint dwSize;
            public IntPtr hwndParent;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string szTitle;
            public IntPtr pCertContext;
            public IntPtr rgszPurposes;
            public uint cPurposes;
            public IntPtr pCryptProviderData;
            public IntPtr hWVTStateData;
            public bool fpCryptProviderDataTrustedUsage;
            public uint idxSigner;
            public uint idxCert;
            public bool fCounterSigner;
            public uint idxCounterSigner;
            public uint cStores;
            public IntPtr rghStores;
            public uint cPropSheetPages;
            public IntPtr rgPropSheetPages;
            public uint nStartPage;
        }
    }
}
