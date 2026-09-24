using Arbitrage.Distribution;
namespace Arbitrage.Backend.IntegrationTests;
public sealed class SignaturePolicyTests
{
    private const string Thumbprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string TestSubject = "CN=ArbitrageTrading D02 TEST ONLY fixture";
    [Fact] public void Optional_unsigned_is_distinct_from_required_signing()
    {
        var unsigned = new SignatureEvidence("Unsigned");
        Assert.True(AuthenticodeVerifier.MeetsPolicy(unsigned, "Unsigned", null, null, false));
        Assert.False(AuthenticodeVerifier.MeetsPolicy(unsigned, "Authenticode", Thumbprint, "CN=Publisher", true));
        Assert.False(AuthenticodeVerifier.MeetsPolicy(unsigned, "TestEphemeral", Thumbprint, TestSubject, false));
    }
    [Theory] [InlineData("Invalid")] [InlineData("Unsigned")] [InlineData("UnsupportedPlatform")]
    public void Metadata_cannot_override_actual_signature_failure(string state)
    {
        Assert.False(AuthenticodeVerifier.MeetsPolicy(new(state, TestSubject, Thumbprint, "SHA256", true), "Authenticode", Thumbprint, TestSubject, true));
    }
    [Fact] public void Test_signature_never_satisfies_production_trust()
    {
        var test = new SignatureEvidence("Untrusted", TestSubject, Thumbprint, "SHA256", false, true);
        Assert.True(AuthenticodeVerifier.MeetsPolicy(test, "TestEphemeral", Thumbprint, TestSubject, false));
        Assert.False(AuthenticodeVerifier.MeetsPolicy(test, "Authenticode", Thumbprint, TestSubject, false));
        Assert.False(AuthenticodeVerifier.MeetsPolicy(test, "TestEphemeral", Thumbprint, TestSubject, true));
        Assert.False(AuthenticodeVerifier.MeetsPolicy(test with { Subject = "CN=Other" }, "TestEphemeral", Thumbprint, null, false));
    }
    [Theory] [InlineData("hash")] [InlineData("thumbprint")] [InlineData("subject")] [InlineData("timestamp")] [InlineData("digest")]
    public void Production_requires_intact_expected_SHA256_timestamped_signature(string altered)
    {
        var valid = new SignatureEvidence("Valid", "CN=Publisher", Thumbprint, "SHA256", true, true);
        Assert.True(AuthenticodeVerifier.MeetsPolicy(valid, "Authenticode", Thumbprint, "CN=Publisher", true));
        var invalid = altered switch {
            "hash" => valid with { ContentValid = false }, "thumbprint" => valid with { Thumbprint = new string('B',40) },
            "subject" => valid with { Subject = "CN=Other" }, "timestamp" => valid with { Timestamped = false },
            _ => valid with { Algorithm = "SHA1" } };
        Assert.False(AuthenticodeVerifier.MeetsPolicy(invalid, "Authenticode", Thumbprint, "CN=Publisher", true));
    }
}
