using System.Security.Cryptography;

namespace Arbitrage.Application;

public sealed record ExchangeCredentialStatus(bool Configured, string? MaskedKeyId, string? PublicKeyFingerprint,
    DateTimeOffset? ConfiguredAt, DateTimeOffset? UpdatedAt, string StoreCapability, string LastAuthenticationResult,
    Guid Version);

// Secret leases stay inside backend infrastructure/transport. Never serialize this type.
public sealed class ExchangeCredentialLease(string keyId, byte[] pkcs8) : IDisposable
{
    public string KeyId { get; } = keyId;
    public byte[] PrivateKey { get; } = pkcs8;
    public void Dispose() => CryptographicOperations.ZeroMemory(PrivateKey);
    public override string ToString() => "[Exchange credential lease]";
}
public interface IExchangeCredentialStore
{
    ExchangeCredentialStatus Status();
    ExchangeCredentialLease? Open();
    ExchangeCredentialStatus Import(string keyId, string path, bool confirmReplacement, Guid expectedVersion);
    ExchangeCredentialStatus Remove(bool confirmed, Guid expectedVersion);
    void RecordAuthentication(string result);
}
public sealed class CredentialOperationException(string code) : Exception(code);
