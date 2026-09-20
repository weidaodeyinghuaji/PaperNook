using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PaperTodo;

internal sealed record AuthenticodeVerification(
    bool IsTrusted,
    string Failure,
    string PublisherSubject,
    string PublisherSpkiSha256,
    string ProductVersion)
{
    internal static AuthenticodeVerification Trusted(
        string publisherSubject,
        string publisherSpkiSha256,
        string productVersion) =>
        new(true, "", publisherSubject, publisherSpkiSha256, productVersion);

    internal static AuthenticodeVerification Rejected(string failure) =>
        new(false, failure, "", "", "");
}

internal interface IAuthenticodeVerifier
{
    AuthenticodeVerification Verify(string path, ReleaseTrust trust);
}

internal sealed class AuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public AuthenticodeVerification Verify(string path, ReleaseTrust trust)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(trust);
        if (!trust.IsConfigured) return AuthenticodeVerification.Rejected("Release trust is not configured.");
        if (!OperatingSystem.IsWindows()) return AuthenticodeVerification.Rejected("Authenticode verification requires Windows.");
        if (!File.Exists(path)) return AuthenticodeVerification.Rejected("Signed file does not exist.");

        var fileInfo = new WinTrustFileInfo(path);
        var data = new WinTrustData(fileInfo);
        try
        {
            var status = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            if (status != 0)
                return AuthenticodeVerification.Rejected($"WinVerifyTrust rejected the file (0x{status:X8}).");

#pragma warning disable SYSLIB0057 // Authenticode PE files do not have an X509CertificateLoader replacement.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var subject = certificate.Subject;
            if (!string.Equals(subject, trust.PublisherSubject, StringComparison.Ordinal))
                return AuthenticodeVerification.Rejected("Authenticode publisher subject is not trusted.");

            var spki = ReleaseTrust.ComputeSpkiSha256(certificate);
            if (!trust.AllowedPublisherSpkiSha256.Contains(spki, StringComparer.Ordinal))
                return AuthenticodeVerification.Rejected("Authenticode publisher SPKI is not trusted.");

            var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim() ?? "";
            var metadataSeparator = productVersion.IndexOf('+');
            if (metadataSeparator >= 0) productVersion = productVersion[..metadataSeparator];
            if (string.IsNullOrWhiteSpace(productVersion))
                return AuthenticodeVerification.Rejected("Signed executable has no product version.");

            return AuthenticodeVerification.Trusted(subject, spki, productVersion);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return AuthenticodeVerification.Rejected(ex.Message);
        }
        finally
        {
            data.Dispose();
            fileInfo.Dispose();
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, SetLastError = true)]
    private static extern uint WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly IntPtr _filePath;

        internal WinTrustFileInfo(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            _filePath = Marshal.StringToCoTaskMemUni(path);
            FilePath = _filePath;
        }

        internal uint StructSize;
        internal IntPtr FilePath;
        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;

        public void Dispose() => Marshal.FreeCoTaskMem(_filePath);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData : IDisposable
    {
        private IntPtr _fileInfo;

        internal WinTrustData(WinTrustFileInfo fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 1;
            UnionChoice = 1;
            _fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, _fileInfo, false);
            File = _fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00000080;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }

        internal uint StructSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr File;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;

        public void Dispose()
        {
            if (_fileInfo == IntPtr.Zero) return;
            Marshal.DestroyStructure<WinTrustFileInfo>(_fileInfo);
            Marshal.FreeCoTaskMem(_fileInfo);
            _fileInfo = IntPtr.Zero;
        }
    }
}
