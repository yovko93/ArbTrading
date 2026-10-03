using System.IO;
using Arbitrage.Desktop.Services;

namespace Arbitrage.Desktop.Tests;

public sealed class BackendArtifactLocatorTests
{
    [Theory]
    [InlineData("Debug", null)]
    [InlineData("Release", null)]
    [InlineData("Debug", "win-x64")]
    [InlineData("Release", "win-x64")]
    public void Repository_build_resolves_matching_configuration_and_runtime(string configuration, string? runtime)
    {
        var root = RealtimeProcessTests.RepositoryRoot();
        var desktop = Path.Combine(root, "src", "Arbitrage.Desktop", "bin", configuration, "net10.0-windows");
        var backend = Path.Combine(root, "src", "Arbitrage.Backend", "bin", configuration, "net10.0");
        if (runtime is not null)
        {
            desktop = Path.Combine(desktop, runtime);
            backend = Path.Combine(backend, runtime);
        }
        Assert.Equal(Path.Combine(backend, "Arbitrage.Backend.exe"), BackendArtifactLocator.Resolve(desktop, false, null));
    }

    [Fact]
    public void Override_package_and_unrelated_directory_resolution_remain_independent()
    {
        var root = RealtimeProcessTests.RepositoryRoot();
        var desktop = Path.Combine(root, "src", "Arbitrage.Desktop", "bin", "Release", "net10.0-windows");
        Assert.Equal(Path.Combine(desktop, "backend", "Arbitrage.Backend.exe"), BackendArtifactLocator.Resolve(desktop, true, null));
        Assert.Equal("explicit.dll", BackendArtifactLocator.Resolve(desktop, false, "explicit.dll"));
        Assert.Equal("explicit.dll", BackendArtifactLocator.Resolve(desktop, true, "explicit.dll"));
        Assert.Equal(Path.Combine(root, "Arbitrage.Backend.exe"), BackendArtifactLocator.Resolve(root, false, null));
    }
}
