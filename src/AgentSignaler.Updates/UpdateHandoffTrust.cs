using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace AgentSignaler.Updates;

// Same renewal-safe publisher/EKU/timestamp policy as UpdaterPolicy.ps1. No leaf pin and
// no configurable allow-any-signer fallback. WinTrust validates the timestamp and chain.
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateScriptTrust : IUpdateScriptTrust
{
    public const string ReleasePublisher = UpdateHandoffPolicy.ReleasePublisher;
    public Task VerifyAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdatePath.RequireNoReparsePoints(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1,
            UnionChoice = 1, File = pointer, StateAction = 1, ProviderFlags = 0x80
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0)
                throw new UnauthorizedAccessException("update_script_signature");
            var provider = WTHelperProvDataFromStateData(data.StateData);
            var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var timestamp = WTHelperGetProvSignerFromChain(provider, 0, true, 0);
            if (provider == IntPtr.Zero || signer == IntPtr.Zero || timestamp == IntPtr.Zero)
                throw new UnauthorizedAccessException("update_script_timestamp");
            var certificate = WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero) throw new UnauthorizedAccessException("update_script_certificate");
            var context = Marshal.PtrToStructure<ProviderCertificate>(certificate).Certificate;
            using var cert = new X509Certificate2(context);
            var usages = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .SelectMany(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()).Select(oid => oid.Value ?? "").ToImmutableArray();
            UpdateHandoffPolicy.RequireSignature(new(true, true, cert.GetNameInfo(X509NameType.SimpleName, false),
                cert.Subject == cert.Issuer, usages));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle; public IntPtr Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr Policy; public IntPtr Sip; public uint UiChoice; public uint RevocationChecks;
        public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData;
        public IntPtr Url; public uint ProviderFlags; public uint UiContext; public IntPtr Settings;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate { public uint Size; public IntPtr Certificate; }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint index);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
}