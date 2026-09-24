#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Arbitrage.Distribution;

public sealed record SignatureEvidence(string State, string? Subject = null, string? Thumbprint = null,
    string? Algorithm = null, bool Timestamped = false, bool ContentValid = false);

// Uses the Windows Authenticode provider, never installs certificates or retrieves network URLs.
public static class AuthenticodeVerifier
{
    public static readonly string[] SigningFiles = new[] {
        "Arbitrage.Desktop.exe", "Arbitrage.Desktop.dll", "Arbitrage.Contracts.dll",
        "backend/Arbitrage.Backend.exe", "backend/Arbitrage.Backend.dll", "backend/Arbitrage.Contracts.dll",
        "backend/Arbitrage.Domain.dll", "backend/Arbitrage.Application.dll", "backend/Arbitrage.Infrastructure.dll",
        "backend/Arbitrage.Connectors.dll", "backend/Arbitrage.Execution.dll", "backend/Arbitrage.Strategies.dll" };

    public static SignatureEvidence Inspect(string path)
    {
        if (!OperatingSystem.IsWindows()) return new("UnsupportedPlatform");
        try
        {
            var verified = Verify(Path.GetFullPath(path), false);
            if (verified.Code == unchecked((int)0x800B0100)) return new("Unsigned");
            var hash = Verify(Path.GetFullPath(path), true);
            var intact = hash.Code == 0;
            var state = verified.Code == 0 && intact ? "Valid" :
                intact && verified.Code is unchecked((int)0x800B0109) or unchecked((int)0x800B010A) ? "Untrusted" : "Invalid";
            return new(state, verified.Subject, verified.Thumbprint, verified.Algorithm, verified.Timestamped, intact);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        { return new("Invalid"); }
    }

    public static bool MeetsPolicy(SignatureEvidence evidence, string mode, string? thumbprint, string? subject, bool requireTimestamp)
    {
        if (mode == "Unsigned") return evidence.State == "Unsigned";
        if (string.IsNullOrWhiteSpace(thumbprint) || !string.Equals(evidence.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(subject) && evidence.Subject != subject) || evidence.Algorithm != "SHA256" ||
            !evidence.ContentValid || (requireTimestamp && !evidence.Timestamped)) return false;
        if (mode == "Authenticode") return evidence.State == "Valid";
        return mode == "TestEphemeral" && evidence.State == "Untrusted" &&
            evidence.Subject is not null && evidence.Subject.StartsWith("CN=ArbitrageTrading D02 TEST ONLY ", StringComparison.Ordinal);
    }

    private sealed record NativeResult(int Code, string? Subject, string? Thumbprint, string? Algorithm, bool Timestamped);
    private static NativeResult Verify(string path, bool hashOnly)
    {
        var file = new FileInfoNative { Size = (uint)Marshal.SizeOf<FileInfoNative>(), Path = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoNative>());
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = filePointer,
            StateAction = 1, Flags = 0x1000 | 0x10 | (hashOnly ? 0x200u : 0u) };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            string? subject = null, thumbprint = null, algorithm = null; bool timestamped = false;
            if (data.State != IntPtr.Zero)
            {
                var provider = WTHelperProvDataFromStateData(data.State);
                var signerPointer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                if (signerPointer != IntPtr.Zero)
                {
                    var signer = Marshal.PtrToStructure<ProviderSigner>(signerPointer);
                    timestamped = signer.CounterSigners > 0;
                    if (signer.Signer != IntPtr.Zero)
                    {
                        var info = Marshal.PtrToStructure<SignerInfo>(signer.Signer);
                        algorithm = Marshal.PtrToStringAnsi(info.Hash.ObjectId) == "2.16.840.1.101.3.4.2.1" ? "SHA256" : "Unsupported";
                    }
                    if (signer.CertificateCount > 0 && signer.Certificates != IntPtr.Zero)
                    {
                        var certificatePointer = Marshal.ReadIntPtr(signer.Certificates, IntPtr.Size == 8 ? 8 : 4);
                        var context = Marshal.PtrToStructure<CertificateContext>(certificatePointer);
                        var bytes = new byte[checked((int)context.Length)]; Marshal.Copy(context.Encoded, bytes, 0, bytes.Length);
                        using var certificate = X509CertificateLoader.LoadCertificate(bytes);
                        subject = certificate.Subject; thumbprint = certificate.Thumbprint;
                    }
                }
            }
            return new(result, subject, thumbprint, algorithm, timestamped);
        }
        finally
        {
            data.StateAction = 2; _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<FileInfoNative>(filePointer); Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoNative { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File;
        public uint StateAction; public IntPtr State, Url; public uint Flags, Context; public IntPtr SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        public uint Size; public System.Runtime.InteropServices.ComTypes.FILETIME VerifiedAt; public uint CertificateCount;
        public IntPtr Certificates; public uint Type; public IntPtr Signer; public uint Error, CounterSigners;
        public IntPtr CounterSignerArray, ChainContext;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Length; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct AlgorithmIdentifier { public IntPtr ObjectId; public Blob Parameters; }
    [StructLayout(LayoutKind.Sequential)] private struct SignerInfo { public uint Version; public Blob Issuer, Serial; public AlgorithmIdentifier Hash; }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContext { public uint Encoding; public IntPtr Encoded; public uint Length; public IntPtr Info, Store; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
}


