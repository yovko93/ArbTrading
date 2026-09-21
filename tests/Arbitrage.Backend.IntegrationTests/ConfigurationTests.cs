using Arbitrage.Backend;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Configuration;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("http://0.0.0.0:5274")]
    [InlineData("http://*:5274")]
    [InlineData("http://+:5274")]
    [InlineData("http://192.168.1.1:5274")]
    [InlineData("http://[::]:5274")]
    [InlineData("http://localhost:5274")]
    [InlineData("https://127.0.0.1:5274")]
    [InlineData("http://127.0.0.1:5274?credential=bad")]
    [InlineData("http://name:password@127.0.0.1:5274")]
    [InlineData("http://127.0.0.1:5274/path")]
    [InlineData("http://127.0.0.1:80")]
    public void Unsafe_endpoints_are_rejected(string url) => Assert.Throws<InvalidOperationException>(() => LocalPaths.ValidateBaseUrl(url));

    [Theory]
    [InlineData("http://127.0.0.1:5274")]
    [InlineData("http://[::1]:5274")]
    public void Literal_loopback_is_supported(string url) => Assert.Equal(new Uri(url), LocalPaths.ValidateBaseUrl(url));

    [Theory]
    [InlineData("urls")]
    [InlineData("http_ports")]
    [InlineData("https_ports")]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("DOTNET_URLS")]
    [InlineData("Kestrel:Endpoints:Http:Url")]
    [InlineData("ASPNETCORE_HTTP_PORTS")]
    public void Host_binding_overrides_cannot_bypass_validation(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "http://0.0.0.0:8080" }).Build();
        Assert.Throws<InvalidOperationException>(() => new LocalOptions().Validate(configuration));
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("Automatic")]
    [InlineData("Unknown")]
    public void Unavailable_execution_is_not_silently_downgraded(string mode) =>
        Assert.Throws<InvalidOperationException>(() => new LocalOptions { TradingMode = mode }.Validate(new ConfigurationBuilder().Build()));

    [Fact]
    public void Server_mode_is_rejected() => Assert.Throws<InvalidOperationException>(() =>
        new LocalOptions { DeploymentMode = "Server" }.Validate(new ConfigurationBuilder().Build()));

    [Fact]
    public void Default_configuration_is_local_paper()
    {
        var options = new LocalOptions(); var endpoint = options.Validate(new ConfigurationBuilder().Build());
        Assert.Equal("127.0.0.1", endpoint.Host); Assert.Equal("Paper", options.TradingMode); Assert.Equal("Local", options.DeploymentMode);
    }
}
