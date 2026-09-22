using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class MarketExplorerStateTests
{
    private sealed class ConnectionFile : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) =>
            Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('a', 64)));
        public Task WriteAsync(LocalConnection value, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> OldEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseOld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DenySave;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Put) return new(DenySave ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new CatalogStatusResponse([], "scope", DateTimeOffset.UtcNow)) };
            if (request.RequestUri.AbsolutePath.EndsWith("/markets", StringComparison.Ordinal))
            {
                var old = request.RequestUri.Query.Contains("search=old", StringComparison.Ordinal);
                if (old) { OldEntered.TrySetResult(true); await ReleaseOld.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                var item = new MarketResponse("Kalshi", "Production", old ? "old" : "new", null,
                    null, null, null, old ? "Old result" : "New result", null, null, [], "open", "Open", [],
                    null, null, null, null, null, null, null, null, null, DateTimeOffset.UtcNow, [], false);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new MarketPageResponse([item], 1, 1, 30, "scope", [])) };
            }
            return new(HttpStatusCode.NotFound);
        }
    }
    private static (MainViewModel State, MarketExplorerViewModel Explorer) Setup(Handler handler)
    {
        var client = new BackendClient(new HttpClient(handler), new ConnectionFile());
        var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new ApplicationSnapshotResponse(1, Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, new SessionResponse(Guid.NewGuid(), workspace, "Local", Capabilities.Phase01A),
            new SystemStatusResponse("test", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
            new WorkspaceSettingsResponse(workspace, "Personal"), []), "http://127.0.0.1:5274");
        return (state, new MarketExplorerViewModel(state, client));
    }

    [Fact]
    public async Task Slow_old_search_cannot_replace_newer_results()
    {
        var handler = new Handler(); var (state, explorer) = Setup(handler);
        using (state) using (explorer)
        {
            explorer.Search = "old";
            var old = explorer.RefreshCommand.ExecuteAsync(null);
            await handler.OldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            explorer.Search = "new";
            await explorer.ApplyFiltersCommand.ExecuteAsync(null);
            handler.ReleaseOld.TrySetResult(true);
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("new", explorer.Markets.Single().NativeId);
        }
    }

    [Fact]
    public async Task Real_save_denial_clears_catalog_and_rejects_old_search_completion()
    {
        var handler = new Handler { DenySave = true }; var (state, explorer) = Setup(handler);
        using (state) using (explorer)
        {
            explorer.Search = "old";
            var old = explorer.RefreshCommand.ExecuteAsync(null);
            await handler.OldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            state.WorkspaceName = "Unsaved draft";
            await state.SaveCommand.ExecuteAsync(null);
            Assert.Equal("AuthorizationDenied", state.ConnectionStatus);
            handler.ReleaseOld.TrySetResult(true);
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(explorer.Markets);
            Assert.Null(explorer.Detail);
        }
    }
}
