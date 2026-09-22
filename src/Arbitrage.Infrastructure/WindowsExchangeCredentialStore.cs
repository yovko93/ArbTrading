using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.LocalTransport;

namespace Arbitrage.Infrastructure;

// Windows CurrentUser DPAPI is intentionally unavailable on server/Linux deployments.
public sealed class WindowsExchangeCredentialStore(string directory, TimeProvider clock) : IExchangeCredentialStore
{
    private readonly object gate = new();
    private readonly string path = Path.Combine(directory, "kalshi.dpapi");
    private string authentication = "NotAttempted";
    private sealed record Stored(string KeyId, byte[] Key, string Fingerprint, DateTimeOffset Created, DateTimeOffset Updated, Guid Version);
    public ExchangeCredentialStatus Status()
    {
        lock (gate)
        {
            if (!OperatingSystem.IsWindows()) return Empty("UnsupportedPlatform");
            var stored = Read();
            if (stored is null) return Empty("WindowsCurrentUserDpapi");
            try { return Metadata(stored); }
            finally { CryptographicOperations.ZeroMemory(stored.Key); }
        }
    }
    public ExchangeCredentialLease? Open()
    {
        lock (gate)
        {
            var stored = Read();
            return stored is null ? null : new(stored.KeyId, stored.Key);
        }
    }
    public ExchangeCredentialStatus Import(string keyId, string file, bool confirmReplacement, Guid expectedVersion)
    {
        lock (gate)
        {
            if (!OperatingSystem.IsWindows()) throw new CredentialOperationException("UnsupportedPlatform");
            var current = Status();
            if (current.Version != expectedVersion || current.Configured && !confirmReplacement)
                throw new CredentialOperationException("ConfirmationRequired");
            if (string.IsNullOrEmpty(keyId) || keyId.Length is < 8 or > 128 || keyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new CredentialOperationException("InvalidKeyId");
            byte[]? raw = null; char[]? pem = null; byte[]? key = null;
            try
            {
                if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file)) throw new CredentialOperationException("InvalidKeyFile");
                ProtectedStorage.RejectLinks(file);
                ProtectedStorage.VerifyPrivateFile(file);
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length is < 32 or > 32_768) throw new CredentialOperationException("InvalidKeyFile");
                    raw = new byte[(int)input.Length]; input.ReadExactly(raw);
                }
                pem = Encoding.UTF8.GetChars(raw);
                using var rsa = RSA.Create();
                rsa.ImportFromPem(pem);
                if (rsa.KeySize is < 2048 or > 4096) throw new CredentialOperationException("UnsupportedKeySize");
                key = rsa.ExportPkcs8PrivateKey();
                var challenge = RandomNumberGenerator.GetBytes(32);
                var signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                if (!rsa.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    throw new CredentialOperationException("InvalidPrivateKey");
                var fingerprint = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
                var stored = new Stored(keyId, key, fingerprint, current.ConfiguredAt ?? clock.GetUtcNow(), clock.GetUtcNow(), Guid.NewGuid());
                Write(stored); authentication = "NotAttempted";
                return Metadata(stored);
            }
            catch (CredentialOperationException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            { throw new CredentialOperationException("InvalidOrUnsafeKeyFile"); }
            finally
            {
                if (raw is not null) CryptographicOperations.ZeroMemory(raw);
                if (pem is not null) Array.Clear(pem);
                if (key is not null) CryptographicOperations.ZeroMemory(key);
            }
        }
    }
    public ExchangeCredentialStatus Remove(bool confirmed, Guid expectedVersion)
    {
        lock (gate)
        {
            var current = Status();
            if (!confirmed || current.Version != expectedVersion) throw new CredentialOperationException("ConfirmationRequired");
            ProtectedStorage.RejectLinks(path);
            if (File.Exists(path)) { ProtectedStorage.VerifyPrivateFile(path); File.Delete(path); }
            authentication = "NotConfigured";
            return Status();
        }
    }
    public void RecordAuthentication(string result)
    {
        lock (gate) authentication = result is "Authenticated" or "AuthenticationFailed" or "NotConfigured" ? result : "NotAttempted";
    }
    private Stored? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        byte[]? plain = null;
        try
        {
            ProtectedStorage.RejectLinks(path);
            if (!File.Exists(path)) return null;
            ProtectedStorage.VerifyPrivateFile(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 65_536) throw new CredentialOperationException("CredentialStoreUnavailable");
            var encrypted = new byte[(int)file.Length]; file.ReadExactly(encrypted);
            plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Stored>(plain) ?? throw new CredentialOperationException("CredentialStoreUnavailable");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { throw new CredentialOperationException("CredentialStoreUnavailable"); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    private void Write(Stored stored)
    {
        if (!OperatingSystem.IsWindows()) throw new CredentialOperationException("UnsupportedPlatform");
        // Defense in depth even when constructed outside the validated host configuration.
        if (!Path.IsPathFullyQualified(directory)) throw new CredentialOperationException("UnsafeSecretDirectory");
        for (var d = new DirectoryInfo(directory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                throw new CredentialOperationException("UnsafeSecretDirectory");
        ProtectedStorage.RejectLinks(path);
        ProtectedStorage.CreatePrivateDirectory(directory);
        if (File.Exists(path)) ProtectedStorage.VerifyPrivateFile(path);
        var plain = JsonSerializer.SerializeToUtf8Bytes(stored);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(encrypted); output.Flush(true); }
            ProtectedStorage.RestrictFile(temporary); ProtectedStorage.VerifyPrivateFile(temporary);
            File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private ExchangeCredentialStatus Metadata(Stored stored) => new(true, "…" + stored.KeyId[^4..], stored.Fingerprint,
        stored.Created, stored.Updated, "WindowsCurrentUserDpapi", authentication, stored.Version);
    private ExchangeCredentialStatus Empty(string capability) => new(false, null, null, null, null, capability, "NotConfigured", Guid.Empty);
}
