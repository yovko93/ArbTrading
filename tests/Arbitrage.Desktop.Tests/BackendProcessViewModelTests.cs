using System.IO;
using System.Net.Http;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

public sealed class BackendProcessViewModelTests
{
    private sealed class Controller : ILocalBackendController
    {
        public int Observations;
        public int Starts;
        public int Stops;
        public string ArtifactExplanation => "Start uses the configured built backend artifact.";
        public Task<LocalBackendObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Observations);
            return Task.FromResult(new LocalBackendObservation(LocalProcessState.NotRunning,
                LocalManagementCapability.ExternalUnmanaged, "No backend is running."));
        }
        public Task<LocalBackendObservation> StartAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref Starts); throw new InvalidOperationException("Refresh launched a process."); }
        public Task<LocalBackendObservation> StopAsync(LocalBackendObservation current, Action<Guid> onAccepted,
            CancellationToken cancellationToken)
        { Interlocked.Increment(ref Stops); throw new InvalidOperationException("Refresh stopped a process."); }
    }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => throw new FileNotFoundException();
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Refresh_observes_without_starting_or_stopping_a_process()
    {
        using var http = new HttpClient();
        var client = new BackendClient(http, new Connection());
        using var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        using var realtime = new RealtimeSession(client, state, new DesktopDiagnostics(), new InlineDispatcher(),
            new RealtimeDelay());
        var controller = new Controller();
        var viewModel = new BackendProcessViewModel(controller, realtime);
        await viewModel.InitializeAsync(default);
        await viewModel.RefreshAsync();
        Assert.Equal(2, controller.Observations);
        Assert.Equal(0, controller.Starts);
        Assert.Equal(0, controller.Stops);
        Assert.Equal("NotRunning", viewModel.ProcessStatus);
        Assert.True(viewModel.CanStart);
        Assert.False(viewModel.CanStop);
    }
}
