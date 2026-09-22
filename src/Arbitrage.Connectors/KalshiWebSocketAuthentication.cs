using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Arbitrage.Application;

namespace Arbitrage.Connectors;

public static class KalshiWebSocketAuthentication
{
    public const string Path = "/trade-api/ws/v2";
    public static IReadOnlyDictionary<string, string> Headers(ExchangeCredentialLease credential, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(credential.PrivateKey, out _);
        var input = Encoding.UTF8.GetBytes(timestamp + "GET" + Path);
        var signature = rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new Dictionary<string, string>
        {
            ["KALSHI-ACCESS-KEY"] = credential.KeyId,
            ["KALSHI-ACCESS-TIMESTAMP"] = timestamp,
            ["KALSHI-ACCESS-SIGNATURE"] = Convert.ToBase64String(signature)
        };
    }
}
