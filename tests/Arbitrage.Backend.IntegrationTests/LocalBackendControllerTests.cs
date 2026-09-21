using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.DependencyInjection;

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

    private sealed class FakeInspector : IManagedProcessInspector
    {
        public ManagedProcessEvidence Evidence = ManagedProcessEvidence.Running;
        public bool Alive = true;
        public int Inspections;
        public ManagedProcessInspection Inspect(ManagedRuntimeMetadata metadata, string expectedExecutable)
        {
            Interlocked.Increment(ref Inspections);
            return Evidence == ManagedProcessEvidence.Running
                ? new(Evidence, new Handle(this)) : new(Evidence);
        }
        private sealed class Handle(FakeInspector owner) : IManagedProcessHandle
        {
            public bool HasExited => !owner.Alive;
            public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(!owner.Alive); }
            public void Dispose() { }
        }
    }

    private sealed class ManagedHarness : IDisposable
    {
        public string Root { get; } = UniqueRoot();
        public string Runtime { get; }
        public string MetadataPath => Path.Combine(Runtime, "managed-local.json");
        public ManagedRuntimeMetadata Metadata { get; }
        public FakeInspector Inspector { get; } = new();
        public LocalBackendController Controller { get; }
        public int StopPosts;
        public bool LoseAcknowledgment;
        public Action? OnStop;
        private readonly HttpClient http;

        public ManagedHarness()
        {
            var workspace = Guid.NewGuid(); var user = Guid.NewGuid(); var profile = Guid.NewGuid();
            var instance = Guid.NewGuid(); var url = $"http://127.0.0.1:{FreePort()}";
            Runtime = Path.Combine(Root, "runtime");
            ProtectedStorage.CreatePrivateDirectory(Runtime);
            var options = new LocalBackendLaunchOptions(Path.Combine(Root, "data"), Runtime, url,
                Path.Combine(Root, "backend.exe"), null);
            Metadata = new(instance, profile, workspace, 424242, DateTimeOffset.UtcNow,
                options.ArtifactPath, options.DataDirectory, options.BaseUrl);
            WriteMetadata(Metadata);
            http = new HttpClient(new Handler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/session", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new SessionResponse(user, workspace, "Local", Capabilities.Phase01A)) };
                if (path.EndsWith("/snapshot", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ApplicationSnapshotResponse(1, instance, profile,
                        DateTimeOffset.UtcNow, new(user, workspace, "Local", Capabilities.Phase01A),
                        new("1", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
                        new(workspace, "Managed"), [])) };
                if (path.EndsWith("/local-runtime/stop", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref StopPosts);
                    OnStop?.Invoke();
                    if (LoseAcknowledgment) throw new HttpRequestException("Test-only lost acknowledgment.");
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new StopLocalRuntimeResponse(instance, "StopRequested")) };
                }
                return new(HttpStatusCode.NotFound);
            }));
            Controller = new LocalBackendController(new BackendClient(http, new Connection(url)), options,
                Inspector, TimeSpan.FromMilliseconds(50));
        }
        public void WriteMetadata(ManagedRuntimeMetadata value)
        {
            File.WriteAllText(MetadataPath, JsonSerializer.Serialize(value));
            ProtectedStorage.RestrictFile(MetadataPath);
        }
        public void Dispose()
        {
            http.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    [Fact]
    public void Production_controller_resolves_from_dependency_injection_without_test_services()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        var services = new ServiceCollection();
        services.AddSingleton(new BackendClient(http, new MissingConnection()));
        services.AddSingleton(new LocalBackendLaunchOptions(Path.Combine(Path.GetTempPath(), "unused-data"),
            Path.Combine(Path.GetTempPath(), "unused-runtime"), "http://127.0.0.1:5274",
            Path.Combine(Path.GetTempPath(), "unused-backend.exe"), null));
        services.AddSingleton<ILocalBackendController, LocalBackendController>();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<LocalBackendController>(provider.GetRequiredService<ILocalBackendController>());
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
            Assert.Equal(LocalProcessState.Unknown, (await controller.StopAsync(observed, _ => { }, default)).ProcessState);
            Assert.Equal(0, stopPosts);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Known_live_process_with_unavailable_endpoint_blocks_duplicate_start()
    {
        using var harness = new ManagedHarness();
        harness.Inspector.Alive = true;
        // The protected metadata and process evidence remain, while HTTP is unavailable.
        harness.LoseAcknowledgment = false;
        var unavailable = new LocalBackendController(new BackendClient(new HttpClient(new Handler(_ =>
            new(HttpStatusCode.ServiceUnavailable))), new Connection(harness.Metadata.BaseUrl)),
            new(harness.Metadata.DataDirectory, harness.Runtime, harness.Metadata.BaseUrl,
                harness.Metadata.ArtifactPath, null), harness.Inspector);
        var observation = await unavailable.ObserveAsync(default);
        Assert.Equal(LocalProcessState.Unknown, observation.ProcessState);
        Assert.Equal(LocalManagementCapability.ManagedLocal, observation.Capability);
        Assert.Equal(LocalProcessState.Unknown, (await unavailable.StartAsync(default)).ProcessState);
    }

    [Fact]
    public async Task Unverified_process_identity_blocks_start_and_stop_without_network_shutdown()
    {
        using var harness = new ManagedHarness();
        var current = await harness.Controller.ObserveAsync(default);
        Assert.Equal(LocalProcessState.Running, current.ProcessState);
        harness.Inspector.Evidence = ManagedProcessEvidence.Unverified;
        Assert.Equal(LocalProcessState.Unknown, (await harness.Controller.ObserveAsync(default)).ProcessState);
        Assert.Equal(LocalProcessState.Unknown, (await harness.Controller.StartAsync(default)).ProcessState);
        Assert.Equal(LocalProcessState.Unknown, (await harness.Controller.StopAsync(current, _ => { }, default)).ProcessState);
        Assert.Equal(0, harness.StopPosts);
    }

    [Fact]
    public async Task Occupied_external_port_blocks_start_but_stopped_managed_process_allows_it()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var root = UniqueRoot();
        try
        {
            var url = $"http://127.0.0.1:{port}";
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
            var controller = new LocalBackendController(new BackendClient(http, new MissingConnection()),
                new(Path.Combine(root, "data"), Path.Combine(root, "runtime"), url,
                    Path.Combine(root, "missing.exe"), null));
            Assert.Equal(LocalProcessState.Unknown, (await controller.ObserveAsync(default)).ProcessState);
            Assert.Equal(LocalProcessState.Unknown, (await controller.StartAsync(default)).ProcessState);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        using var harness = new ManagedHarness();
        harness.Inspector.Evidence = ManagedProcessEvidence.Exited;
        using var unavailableHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        var stopped = new LocalBackendController(new BackendClient(unavailableHttp, new Connection(harness.Metadata.BaseUrl)),
            new(harness.Metadata.DataDirectory, harness.Runtime, harness.Metadata.BaseUrl,
                harness.Metadata.ArtifactPath, null), harness.Inspector);
        Assert.Equal(LocalProcessState.NotRunning, (await stopped.ObserveAsync(default)).ProcessState);
        Assert.Equal(LocalProcessState.Faulted, (await stopped.StartAsync(default)).ProcessState);
    }

    [Theory]
    [InlineData(false, true, LocalProcessState.NotRunning)]
    [InlineData(false, false, LocalProcessState.Unknown)]
    [InlineData(true, true, LocalProcessState.NotRunning)]
    [InlineData(true, false, LocalProcessState.Unknown)]
    public async Task Stop_uses_preacquired_process_handle_for_fast_exit_timeout_and_lost_ack(
        bool loseAcknowledgment, bool exits, LocalProcessState expectedState)
    {
        using var harness = new ManagedHarness();
        var current = await harness.Controller.ObserveAsync(default);
        harness.LoseAcknowledgment = loseAcknowledgment;
        harness.OnStop = () => harness.Inspector.Alive = !exits;
        var accepted = 0;
        var result = await harness.Controller.StopAsync(current, _ => accepted++, default);
        Assert.Equal(expectedState, result.ProcessState);
        Assert.Equal(1, harness.StopPosts);
        Assert.Equal(loseAcknowledgment ? 0 : 1, accepted);
        Assert.Equal(exits, !File.Exists(harness.MetadataPath));
    }

    [Fact]
    public async Task Stop_does_not_delete_replacement_instance_metadata()
    {
        using var harness = new ManagedHarness();
        var current = await harness.Controller.ObserveAsync(default);
        var replacement = harness.Metadata with { BackendInstanceId = Guid.NewGuid() };
        harness.OnStop = () =>
        {
            harness.Inspector.Alive = false;
            harness.WriteMetadata(replacement);
        };
        Assert.Equal(LocalProcessState.NotRunning,
            (await harness.Controller.StopAsync(current, _ => { }, default)).ProcessState);
        Assert.True(File.Exists(harness.MetadataPath));
        Assert.Equal(replacement.BackendInstanceId,
            JsonSerializer.Deserialize<ManagedRuntimeMetadata>(File.ReadAllText(harness.MetadataPath))!.BackendInstanceId);
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
