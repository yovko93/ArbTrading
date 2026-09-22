using System.Net.WebSockets;
using Arbitrage.Connectors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class MarketWebSocketTests
{
    [Fact]
    public async Task Cancelled_receive_preserves_socket_for_unsubscribe_then_closes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", Args = [] });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); app.UseWebSockets();
        var unsubscribed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Map("/", async context =>
        {
            using var accepted = await context.WebSockets.AcceptWebSocketAsync();
            var bytes = new byte[1024];
            var result = await accepted.ReceiveAsync(new ArraySegment<byte>(bytes), context.RequestAborted);
            unsubscribed.TrySetResult(System.Text.Encoding.UTF8.GetString(bytes, 0, result.Count));
            await accepted.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
        });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var socket = new MarketWebSocket(); using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new Uri(address.Replace("http:", "ws:", StringComparison.Ordinal)), new Dictionary<string, string>(), budget.Token);
            using var cancelReceive = new CancellationTokenSource(); var receive = socket.ReceiveAsync(cancelReceive.Token);
            cancelReceive.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
            await socket.SendAsync("unsubscribe-fixture", budget.Token);
            Assert.Equal("unsubscribe-fixture", await unsubscribed.Task.WaitAsync(budget.Token));
            await socket.CloseAsync(budget.Token);
        }
        finally { await app.StopAsync(); }
    }
}
