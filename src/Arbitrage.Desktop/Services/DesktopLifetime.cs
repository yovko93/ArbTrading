namespace Arbitrage.Desktop.Services;

// App owns the provider and external logger; the provider alone owns its services.
public sealed class DesktopLifetime : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private int disposed;
    public CancellationToken Token => cancellation.Token;
    public bool IsCancellationRequested => cancellation.IsCancellationRequested;
    public IDisposable? ServiceProvider { private get; set; }
    public IDisposable? Logger { private get; set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { cancellation.Cancel(); }
        finally
        {
            // Partial startup may have only a logger, or a partially resolved provider.
            // Keep the external logger alive until service disposal has finished.
            try { ServiceProvider?.Dispose(); }
            finally
            {
                try { Logger?.Dispose(); }
                finally { cancellation.Dispose(); }
            }
        }
    }
}
