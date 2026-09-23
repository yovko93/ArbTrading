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

    [Fact]
    public async Task Duplicate_concurrent_wakes_coalesce_to_one_bounded_notification()
    {
        var wake = new CoalescingWake();
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(wake.Signal)));
        Assert.True(await wake.WaitAsync(TimeSpan.FromSeconds(1), default));
        Assert.False(await wake.WaitAsync(TimeSpan.FromMilliseconds(20), default));
    }

    [Fact]
    public async Task Cancelled_wait_detaches_before_a_later_wake_and_does_not_lose_it()
    {
        var wake = new CoalescingWake();
        using var cancelled = new CancellationTokenSource();
        var oldWait = wake.WaitAsync(cancelled.Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWait);
        wake.Signal();
        Assert.True(await wake.WaitAsync(TimeSpan.FromSeconds(1), default));
        Assert.False(await wake.WaitAsync(TimeSpan.FromMilliseconds(20), default));
    }

    [Fact]
    public async Task Pending_wake_dispose_and_stop_then_dispose_are_idempotent()
    {
        var missing = new MissingConnection(); var delay = new CapturingDelay();
        using var http = new HttpClient(new RejectHandler());
        var client = new BackendClient(http, missing);
        using var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        var session = new RealtimeSession(client, state, new DesktopDiagnostics(), new InlineDispatcher(), delay);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => session.RefreshAsync()));
        session.Dispose(); session.Dispose();
        session.Start();
        await session.StopAsync();
        session.Dispose();
        Assert.Equal(0, missing.Reads);
    }

    [Fact]
    public async Task Concurrent_refresh_and_shutdown_during_backoff_are_bounded()
    {
        var missing = new MissingConnection(); var delay = new CapturingDelay();
        using var http = new HttpClient(new RejectHandler());
        var client = new BackendClient(http, missing);
        using var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        var session = new RealtimeSession(client, state, new DesktopDiagnostics(), new InlineDispatcher(), delay);
        session.Start();
        await delay.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var signals = Enumerable.Range(0, 64).Select(_ => Task.Run(async () => await session.RefreshAsync()));
        await Task.WhenAll(signals.Append(Task.Run(session.Dispose)).Append(Task.Run(session.StopAsync)));
        session.Dispose();
    }

    [Fact]
    public async Task Access_pause_wake_refresh_and_dispose_remain_safe()
    {
        var missing = new MissingConnection(); var delay = new CapturingDelay();
        using var http = new HttpClient(new RejectHandler());
        var client = new BackendClient(http, missing);
        using var state = new MainViewModel(client, NullLogger<MainViewModel>.Instance);
        var session = new RealtimeSession(client, state, new DesktopDiagnostics(), new InlineDispatcher(), delay);
        session.Start();
        await delay.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.SetRealtimeStatus("AuthorizationDenied", "Controlled access-pause fixture.");
        await session.RefreshAsync();
        await missing.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Dispose();
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        session.Dispose();
    }
}
