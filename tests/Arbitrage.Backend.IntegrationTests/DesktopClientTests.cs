using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class DesktopClientTests
{
    private sealed class ConnectionFile : ILocalConnectionFile
    {
        public bool Missing { get; set; }
        public int Reads { get; private set; }
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            if (Missing) throw new FileNotFoundException();
            return Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string(Reads == 1 ? 'A' : 'B', 64)));
        }
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public async Task Actual_desktop_commands_refresh_save_and_reload_backend_state()
    {
        await using var app = new BackendFixture(); using var ready = await app.AuthenticatedClientAsync();
        using var http = app.CreateClient();
        var client = new BackendClient(http, new ProtectedLocalConnectionFile(Path.Combine(app.Root, "runtime")));
        using var model = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        await model.InitializeAsync(default);
        Assert.Equal("Connected", model.ConnectionStatus); Assert.Equal("Healthy", model.Persistence);
        Assert.Equal("Paper", model.TradingMode); Assert.Contains("not implemented", model.Execution);
        Assert.Contains("Polymarket: NotImplemented", model.Exchanges); Assert.True(model.CanEdit);
        model.WorkspaceName = "Desktop command test";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Contains("saved and audited", model.Message);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Desktop command test", model.WorkspaceName);
        var snapshot = await client.LoadAsync(default);
        Assert.Equal(snapshot.Session.UserId.ToString(), model.UserId);
        Assert.Equal(snapshot.Workspace.WorkspaceId.ToString(), model.WorkspaceIdentifier);
    }

    [Fact]
    public async Task Refresh_recovers_after_metadata_becomes_available()
    {
        var file = new ConnectionFile { Missing = true };
        using var http = new HttpClient(new Handler((request, _) =>
        {
            object response = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/session" => new SessionResponse(Guid.NewGuid(), Guid.NewGuid(), "Local", Capabilities.Phase01A),
                "/api/v1/system/status" => new SystemStatusResponse("test", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
                "/api/v1/exchanges/status" => Array.Empty<ExchangeStatusResponse>(),
                _ => new WorkspaceSettingsResponse(Guid.NewGuid(), "Fixture")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
        }));
        using var model = new MainViewModel(new BackendClient(http, file), NullLogger<MainViewModel>.Instance);
        await model.InitializeAsync(default);
        Assert.Equal("Disconnected", model.ConnectionStatus); Assert.False(model.CanEdit);
        file.Missing = false;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Connected", model.ConnectionStatus); Assert.True(model.CanEdit);
    }

    [Fact]
    public async Task Authentication_retry_rereads_credential_without_bypassing_authentication()
    {
        var file = new ConnectionFile(); var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(new string(calls == 1 ? 'A' : 'B', 64), request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));
        var failure = await Assert.ThrowsAsync<BackendFailure>(() => new BackendClient(http, file).LoadAsync(default));
        Assert.Equal(ConnectionState.AuthenticationFailed, failure.State); Assert.Equal(2, calls); Assert.Equal(2, file.Reads);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, ConnectionState.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, ConnectionState.AuthenticationFailed)]
    public async Task Failure_states_are_explicit(HttpStatusCode status, ConnectionState expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var failure = await Assert.ThrowsAsync<BackendFailure>(() => new BackendClient(http, new ConnectionFile()).LoadAsync(default));
        Assert.Equal(expected, failure.State);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_reconnect_retry()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) => { requests++; throw new HttpRequestException(); }));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new BackendClient(http, new ConnectionFile()).LoadAsync(new CancellationToken(true)));
        Assert.Equal(0, requests);
    }
}
