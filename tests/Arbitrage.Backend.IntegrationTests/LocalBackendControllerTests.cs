using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.LocalTransport;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class LocalBackendControllerTests
{
    private sealed class MissingConnection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => throw new FileNotFoundException();
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Connection(string url) : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LocalConnection(url, new string('A', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }

    [Fact]
    public async Task Missing_artifact_reports_actionable_error_without_launching_a_shell()
    {
        var root = UniqueRoot();
        try
        {
            var url = $"http://127.0.0.1:{FreePort()}";
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
            var controller = new LocalBackendController(new BackendClient(http, new MissingConnection()),
                new(Path.Combine(root, "data"), Path.Combine(root, "runtime"), url,
                    Path.Combine(root, "absent-backend.exe"), null));
            var result = await controller.StartAsync(default);
            Assert.Equal(LocalProcessState.Faulted, result.ProcessState);
            Assert.Contains("ARBITRAGE_BACKEND_ARTIFACT", result.Explanation);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Reachable_external_backend_is_observable_but_never_stopped_or_duplicated()
    {
        var root = UniqueRoot(); var workspace = Guid.NewGuid(); var user = Guid.NewGuid();
        var url = $"http://127.0.0.1:{FreePort()}"; var stopPosts = 0;
        try
        {
            using var http = new HttpClient(new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/session", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new SessionResponse(user, workspace, "Local", Capabilities.Phase01A)) };
                if (request.RequestUri.AbsolutePath.EndsWith("/snapshot", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ApplicationSnapshotResponse(1, Guid.NewGuid(), Guid.NewGuid(),
                        DateTimeOffset.UtcNow, new(user, workspace, "Local", Capabilities.Phase01A),
                        new("1", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
                        new(workspace, "External"), [])) };
                stopPosts++; return new(HttpStatusCode.OK);
            }));
            var controller = new LocalBackendController(new BackendClient(http, new Connection(url)),
                new(Path.Combine(root, "data"), Path.Combine(root, "runtime"), url,
                    Path.Combine(root, "absent-backend.exe"), null));
            var observed = await controller.ObserveAsync(default);
            Assert.Equal(LocalProcessState.Running, observed.ProcessState);
            Assert.Equal(LocalManagementCapability.ExternalUnmanaged, observed.Capability);
            Assert.Equal(LocalProcessState.Running, (await controller.StartAsync(default)).ProcessState);
            Assert.Equal(LocalProcessState.Unknown, (await controller.StopAsync(observed, () => { }, default)).ProcessState);
            Assert.Equal(0, stopPosts);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string UniqueRoot()
    {
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ArbitrageTrading-01C-controller")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, Guid.NewGuid().ToString("N")));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test path.");
        return path;
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}
