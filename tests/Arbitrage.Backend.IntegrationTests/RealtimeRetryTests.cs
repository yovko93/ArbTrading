using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class RealtimeRetryTests
{
    private sealed class MissingConnection : ILocalConnectionFile
    {
        private int reads;
        public TaskCompletionSource<bool> FirstRead { get; } = NewSignal();
        public TaskCompletionSource<bool> SecondRead { get; } = NewSignal();
        public int Reads => Volatile.Read(ref reads);
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref reads);
            if (count == 1) FirstRead.TrySetResult(true);
            if (count == 2) SecondRead.TrySetResult(true);
            throw new FileNotFoundException();
        }
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }
    private sealed class CapturingDelay : IRealtimeDelay
    {
        public TaskCompletionSource<bool> Called { get; } = NewSignal();
        public TimeSpan Requested { get; private set; }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Requested = delay; Called.TrySetResult(true);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
    private sealed class RejectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP must not be reached without protected metadata.");
    }
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Initial_failure_uses_bounded_backoff_manual_refresh_retries_and_shutdown_cancels()
    {
        var missing = new MissingConnection(); var delay = new CapturingDelay();
        using var http = new HttpClient(new RejectHandler());
        var client = new BackendClient(http, missing);
        using var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        using var session = new RealtimeSession(client, state, new DesktopDiagnostics(), new InlineDispatcher(), delay);
        session.Start();
        await missing.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delay.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(delay.Requested.TotalSeconds, 2, 2.5);
        Assert.Equal(1, missing.Reads);
        await session.RefreshAsync();
        await missing.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync();
        Assert.Equal(2, missing.Reads);
    }
}
