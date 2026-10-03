using System.IO;
using Arbitrage.Desktop.Services;

namespace Arbitrage.Desktop.Tests;

public sealed class BackendArtifactLocatorTests
{
    [Fact]
    public void Repository_build_resolves_matching_backend_without_copying()
    {
        var backend = RealtimeProcessTests.BackendArtifact();
        var root = new DirectoryInfo(Path.GetDirectoryName(backend)!).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var desktop = Path.Combine(root, "src", "Arbitrage.Desktop", "bin", "Release", "net10.0-windows");
        Assert.Equal(backend, BackendArtifactLocator.Resolve(desktop, false, null));
        Assert.Equal(Path.Combine(desktop, "backend", "Arbitrage.Backend.exe"), BackendArtifactLocator.Resolve(desktop, true, null));
        Assert.Equal("explicit.dll", BackendArtifactLocator.Resolve(desktop, false, "explicit.dll"));
        Assert.Equal("explicit.dll", BackendArtifactLocator.Resolve(desktop, true, "explicit.dll"));
        Assert.Equal(Path.Combine(root, "Arbitrage.Backend.exe"), BackendArtifactLocator.Resolve(root, false, null));
        Assert.EndsWith(Path.Combine("Debug", "net10.0", "Arbitrage.Backend.exe"), BackendArtifactLocator.Resolve(desktop.Replace("Release", "Debug"), false, null));
        Assert.EndsWith(Path.Combine("Release", "net10.0", "win-x64", "Arbitrage.Backend.exe"), BackendArtifactLocator.Resolve(Path.Combine(desktop, "win-x64"), false, null));
    }
}
