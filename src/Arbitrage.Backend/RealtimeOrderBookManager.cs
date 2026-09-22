using Arbitrage.Application;
using Arbitrage.Connectors;
using Arbitrage.Domain;

namespace Arbitrage.Backend;

public sealed class RealtimeOrderBookManager(RealtimeOrderBookSource source, OrderBookCache cache,
    RealtimePublisher publisher, LocalOptions options, TimeProvider clock) : BackgroundService
{
    private sealed class Owner
    {
        public Dictionary<OrderBookInstrumentId, (OrderBookRequest Request, Guid Workspace)> Desired { get; } = [];
        public CancellationTokenSource? Cancellation;
        public Task? Running;
        public long Revision, StartedRevision = -1;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Owner> owners = new() { ["Polymarket"] = new(), ["Kalshi"] = new() };
    private readonly Dictionary<OrderBookInstrumentId, (Guid Workspace, long Version, string State)> notified = [];
    public void Start(Guid workspace, OrderBookRequest request)
    {
        lock (gate)
        {
            var owner = owners[request.Instrument.Exchange];
            if (request.Instrument.Exchange == "Kalshi" && !request.BinarySupported)
            {
                if (owner.Desired.TryGetValue(request.Instrument, out var occupied) && occupied.Workspace != workspace)
                    throw new ArgumentException("SubscriptionOwnerMismatch");
                Stop(workspace, request.Instrument);
                cache.PublishRealtime(request.Instrument, new(cache.Read(request.Instrument).Realtime?.Generation ?? 0,
                    RealtimeSubscriptionState.Unsupported, BookContinuity.Disconnected, ResyncReason: "UnsupportedMarketStructure"));
                return;
            }
            if (owner.Desired.TryGetValue(request.Instrument, out var existing))
            {
                if (existing.Workspace != workspace) throw new ArgumentException("SubscriptionOwnerMismatch");
                if (owner.Running?.IsCompleted == true) Restart(owner); // Explicit recovery, never automatic auth retry.
                return;
            }
            if (owners.Values.Sum(o => o.Desired.Count) >= Math.Min(options.RealtimeMaximumInstruments, options.OrderBookCacheCapacity))
                throw new InvalidOperationException("RealtimeCapacity");
            owner.Desired.Add(request.Instrument, (request, workspace));
            var previous = cache.Read(request.Instrument).Realtime;
            cache.PublishRealtime(request.Instrument, new(previous?.Generation ?? 0,
                RealtimeSubscriptionState.Connecting, BookContinuity.AwaitingAnchor));
            Restart(owner);
        }
    }
    public void Stop(Guid workspace, OrderBookInstrumentId id)
    {
        lock (gate)
        {
            var owner = owners[id.Exchange];
            if (!owner.Desired.TryGetValue(id, out var entry) || entry.Workspace != workspace) return;
            owner.Desired.Remove(id); Restart(owner);
            var previous = cache.Read(id).Realtime;
            if (previous is not null) cache.PublishRealtime(id, previous with { State = RealtimeSubscriptionState.Stopped,
                Continuity = BookContinuity.Disconnected, Connected = false, AnchorAt = null, ResyncReason = "Stopped" });
            publisher.OrderBookChanged(workspace, id, cache.Read(id));
            notified.Remove(id);
        }
    }
    private void Restart(Owner owner)
    {
        owner.Revision++; owner.Cancellation?.Cancel();
        foreach (var id in owner.Desired.Keys)
        {
            var previous = cache.Read(id).Realtime;
            if (previous is not null) cache.PublishRealtime(id, previous with { State = RealtimeSubscriptionState.Resynchronizing,
                Connected = false, Continuity = BookContinuity.Resynchronizing, AnchorAt = null, ResyncReason = "SubscriptionSetChanged" });
        }
    }
    public void CredentialsChanged()
    {
        lock (gate)
        {
            foreach (var entry in owners["Kalshi"].Desired.ToArray()) Stop(entry.Value.Workspace, entry.Key);
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                lock (gate)
                {
                    foreach (var (exchange, owner) in owners)
                    {
                        if (owner.Revision != owner.StartedRevision && (owner.Running is null || owner.Running.IsCompleted))
                        {
                            owner.Cancellation?.Dispose();
                            owner.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            owner.StartedRevision = owner.Revision;
                            var selected = owner.Desired.Values.Select(v => v.Request).ToArray();
                            if (selected.Length > 0)
                                owner.Running = Task.Run(() => source.RunAsync(exchange, selected, (id, metadata, book) =>
                                {
                                    lock (gate)
                                    {
                                        if (!owner.Desired.ContainsKey(id) || owner.Revision != owner.StartedRevision) return;
                                        cache.PublishRealtime(id, metadata, book);
                                    }
                                }, owner.Cancellation.Token), CancellationToken.None);
                        }
                        foreach (var (id, value) in owner.Desired)
                        {
                            var current = cache.Read(id);
                            var state = current.Realtime?.State.ToString() ?? "Connecting";
                            if (!notified.TryGetValue(id, out var last) || last.Version != current.Version || last.State != state)
                            {
                                publisher.OrderBookChanged(value.Workspace, id, current);
                                if (last.State != state && current.Realtime?.ResyncReason is not null)
                                    publisher.Diagnostic(value.Workspace, "Warning", "Realtime", state, "Market-data subscription state changed.");
                                notified[id] = (value.Workspace, current.Version, state);
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Task[] running;
            lock (gate)
            {
                foreach (var owner in owners.Values) owner.Cancellation?.Cancel();
                running = owners.Values.Where(o => o.Running is not null).Select(o => o.Running!).ToArray();
            }
            await Task.WhenAll(running);
            foreach (var owner in owners.Values) owner.Cancellation?.Dispose();
        }
    }
}
