using Arbitrage.LocalTransport;

namespace Arbitrage.Backend;

public sealed class LocalOptions
{
    public string DeploymentMode { get; set; } = "Local";
    public string TradingMode { get; set; } = "Paper";
    public string BaseUrl { get; set; } = "http://127.0.0.1:5274";
    public string DataDirectory { get; set; } = Path.Combine(LocalPaths.Root, "backend");
    public string RuntimeDirectory { get; set; } = LocalPaths.Runtime;
    public bool ManagedLocal { get; set; }
    public int HeartbeatSeconds { get; set; } = 15;
    public int DiscoveryPageSize { get; set; } = 200;
    public int DiscoveryRunMinutes { get; set; } = 15;
    public int PolymarketRequestIntervalMs { get; set; } = 300;
    public int KalshiRequestIntervalMs { get; set; } = 300;
    public int RealtimeMaximumInstruments { get; set; } = 16;
    public int RealtimeFreshnessSeconds { get; set; } = 10;
    public int OrderBookFreshnessSeconds { get; set; } = 5;
    public int OrderBookCacheCapacity { get; set; } = 128;

    public Uri Validate(IConfiguration configuration)
    {
        if (RealtimeMaximumInstruments is < 1 or > 32 || RealtimeFreshnessSeconds is < 1 or > 60) throw new InvalidOperationException("Invalid realtime limits.");
        if (OrderBookFreshnessSeconds is < 1 or > 60 || OrderBookCacheCapacity is < 1 or > 1024)
            throw new InvalidOperationException("Orderbook freshness must be 1–60 seconds and cache capacity 1–1024.");
        if (DeploymentMode != "Local") throw new InvalidOperationException("Server deployment is unavailable in Phase 01A.");
        if (TradingMode != "Paper") throw new InvalidOperationException("Requested execution mode is unavailable in Phase 01A; only the Paper environment is supported.");
        if (HeartbeatSeconds is < 10 or > 300) throw new InvalidOperationException("Heartbeat interval must be 10–300 seconds.");
        if (DiscoveryPageSize is < 1 or > 500 || DiscoveryRunMinutes is < 1 or > 120)
            throw new InvalidOperationException("Discovery page size must be 1–500 and run budget 1–120 minutes.");
        if (PolymarketRequestIntervalMs is < 100 or > 10_000 || KalshiRequestIntervalMs is < 100 or > 10_000)
            throw new InvalidOperationException("Public market request intervals must be 100–10,000 milliseconds.");
        foreach (var key in new[] { "urls", "http_ports", "https_ports", "ASPNETCORE_URLS", "DOTNET_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS" })
            if (!string.IsNullOrWhiteSpace(configuration[key]))
                throw new InvalidOperationException("Host binding overrides are not supported. Configure Local:BaseUrl with a literal loopback address.");
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("Kestrel endpoint overrides are not supported in Local mode.");
        if (!Path.IsPathFullyQualified(DataDirectory) || !Path.IsPathFullyQualified(RuntimeDirectory))
            throw new InvalidOperationException("Local storage directories must be absolute paths.");
        DataDirectory = Path.GetFullPath(DataDirectory);
        RuntimeDirectory = Path.GetFullPath(RuntimeDirectory);
        foreach (var path in new[] { DataDirectory, RuntimeDirectory })
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
                    throw new InvalidOperationException("Runtime storage must be outside a Git checkout or worktree.");
        var applicationDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var packageRoot = Path.GetDirectoryName(applicationDirectory);
        if (packageRoot is not null && Path.GetFileName(applicationDirectory).Equals("backend", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (new[] { DataDirectory, RuntimeDirectory }.Any(p => (p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Mutable storage must be outside the portable package.");
        }
        return LocalPaths.ValidateBaseUrl(BaseUrl);
    }
}
