using System.Net.Http.Headers;
using Arbitrage.Infrastructure;
using Arbitrage.LocalTransport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class BackendFixture : WebApplicationFactory<Program>
{
    private readonly Action<IServiceCollection>? configureServices;
    public BackendFixture(Action<IServiceCollection>? configureServices = null) => this.configureServices = configureServices;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-tests", Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (configureServices is not null) builder.ConfigureTestServices(configureServices);
        builder.UseEnvironment("Testing");
        // Host settings are available during builder creation, before startup reads storage options.
        builder.UseSetting("Local:DataDirectory", Path.Combine(Root, "backend"));
        builder.UseSetting("Local:RuntimeDirectory", Path.Combine(Root, "runtime"));
        builder.UseSetting("Local:BaseUrl", "http://127.0.0.1:5274");
    }

    public async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = CreateClient();
        // Production credential transport, not a test authentication scheme.
        var file = new ProtectedLocalConnectionFile(Path.Combine(Root, "runtime"));
        LocalConnection? connection = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try { connection = await file.ReadAsync(default); break; }
            catch (IOException) { await Task.Delay(20); }
        }
        if (connection is null) throw new InvalidOperationException("Backend did not publish connection metadata.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Credential);
        return client;
    }

    public async Task<T> WithDatabaseAsync<T>(Func<TradingDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TradingDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        // WebApplicationFactory stops the host before the entry-point finally releases its lease.
        for (var attempt = 0; Directory.Exists(Root); attempt++)
        {
            try { Directory.Delete(Root, true); }
            catch (IOException) when (attempt < 100) { await Task.Delay(20); }
        }
    }
}
