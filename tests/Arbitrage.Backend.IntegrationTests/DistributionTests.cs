using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Arbitrage.Desktop.Services;
using Arbitrage.LocalTransport;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class DistributionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Arbitrage distribution tests", Guid.NewGuid().ToString("N"));
    private string Package => Path.Combine(root, "package");
    private DistributionManifest manifest;
    public DistributionTests()
    {
        Directory.CreateDirectory(Path.Combine(Package, "backend"));
        File.WriteAllText(Path.Combine(Package, "Arbitrage.Desktop.exe"), "desktop fixture");
        File.WriteAllText(Path.Combine(Package, "backend/Arbitrage.Backend.exe"), "non executable fixture");
        File.WriteAllText(Path.Combine(Package, "backend/appsettings.json"), """{"Local":{"DeploymentMode":"Local","TradingMode":"Paper","BaseUrl":"http://127.0.0.1:5274"}}""");
        var files = Directory.GetFiles(Package, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(Package, p).Replace('\\', '/'), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        manifest = new(1, "ArbitrageTrading", "win-x64", "net10.0-windows", "net10.0", new string('a', 40), true, DateTimeOffset.UtcNow,
            "Arbitrage.Desktop.exe", "backend/Arbitrage.Backend.exe", "backend/appsettings.json", files["Arbitrage.Desktop.exe"], files["backend/Arbitrage.Backend.exe"], files["backend/appsettings.json"], "PortableZip", files);
        WriteManifest();
    }
    private void WriteManifest() => File.WriteAllText(Path.Combine(Package, DistributionPackage.ManifestName), JsonSerializer.Serialize(manifest));
    [Fact] public void Valid_package_and_development_layout_are_distinguished()
    {
        Assert.True(DistributionPackage.Validate(Package, checkPlatform: false).Valid);
        Assert.False(DistributionPackage.Validate(root, checkPlatform: false).IsPackage);
        Assert.False(DistributionPackage.Validate(Package, Path.Combine(root, "other.exe"), false).Valid);
    }
    [Theory] [InlineData("tampered")] [InlineData("missing")] [InlineData("config")] [InlineData("extra")] [InlineData("malformed")] [InlineData("manifestMissing")]
    public void Corrupt_package_is_rejected(string kind)
    {
        var backend = Path.Combine(Package, manifest.BackendRelativePath);
        if (kind == "tampered") File.AppendAllText(backend, "changed");
        if (kind == "missing") File.Delete(backend);
        if (kind == "config") File.AppendAllText(Path.Combine(Package, manifest.BackendConfigRelativePath), "changed");
        if (kind == "extra") File.WriteAllText(Path.Combine(Package, "runtime.db"), "unexpected");
        if (kind == "malformed") File.WriteAllText(Path.Combine(Package, DistributionPackage.ManifestName), "{");
        if (kind == "manifestMissing") File.Delete(Path.Combine(Package, DistributionPackage.ManifestName));
        Assert.False(DistributionPackage.Validate(Package, checkPlatform: false).Valid);
    }
    [Theory] [InlineData("../outside.exe")] [InlineData("C:/outside.exe")] [InlineData("/outside.exe")] [InlineData("backend/../outside.exe")]
    public void Manifest_cannot_select_external_executable(string path)
    {
        manifest = manifest with { BackendRelativePath = path }; WriteManifest();
        Assert.False(DistributionPackage.Validate(Package, checkPlatform: false).Valid);
        Assert.Throws<InvalidDataException>(() => DistributionPackage.Inside(Package, path));
    }
    private sealed class Missing : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new FileNotFoundException();
        public Task WriteAsync(LocalConnection c, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Migration : IBackendMigrationRunner
    {
        public int Calls; public ProcessStartInfo? Start;
        public Task<BackendMigrationResult> RunAsync(ProcessStartInfo start, CancellationToken ct)
        { Calls++; Start = start; return Task.FromResult(new BackendMigrationResult(false, "DatabaseBackupFailed")); }
    }
    [Fact] public async Task Migration_failure_prevents_normal_launch_and_receives_identical_profile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var http = new HttpClient(); var migration = new Migration();
        var options = new LocalBackendLaunchOptions(Path.Combine(root, "data"), Path.Combine(root, "runtime"), $"http://127.0.0.1:{port}", Path.Combine(Package, manifest.BackendRelativePath), null) { PackageRoot = Package };
        var controller = new LocalBackendController(new BackendClient(http, new Missing()), options, migrationRunner: migration);
        Assert.Equal(0, migration.Calls); // Constructing/inspecting never migrates or starts.
        Assert.Equal(LocalProcessState.NotRunning, (await controller.ObserveAsync(default)).ProcessState);
        Assert.Equal(0, migration.Calls);
        var result = await controller.StartAsync(default);
        Assert.Equal(LocalProcessState.Faulted, result.ProcessState); Assert.Equal(1, migration.Calls);
        Assert.Contains("DatabaseBackupFailed", result.Explanation);
        Assert.Equal(options.DataDirectory, migration.Start!.Environment["Local__DataDirectory"]);
        Assert.Equal(options.RuntimeDirectory, migration.Start.Environment["Local__RuntimeDirectory"]);
        Assert.Equal(options.BaseUrl, migration.Start.Environment["Local__BaseUrl"]);
        Assert.Equal("Paper", migration.Start.Environment["Local__TradingMode"]);
        Assert.Equal("Local", migration.Start.Environment["Local__DeploymentMode"]);
        File.AppendAllText(options.ArtifactPath, "tamper");
        Assert.Equal(LocalProcessState.Faulted, (await controller.StartAsync(default)).ProcessState);
        Assert.Equal(1, migration.Calls);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
