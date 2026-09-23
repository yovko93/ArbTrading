using System.Threading.Channels;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend;

// Explicit, global single-flight; ten markets, forty event pages, thirty seconds, four retained job summaries.
public sealed class FeeJobs(IServiceScopeFactory scopes, TimeProvider clock) : BackgroundService
{
    private sealed class Run(Guid actor, Guid workspace, RefreshFeesRequest request)
    {
        public Guid Actor { get; } = actor;
        public Guid Workspace { get; } = workspace;
        public RefreshFeesRequest Request { get; } = request;
        public CancellationTokenSource Cancel { get; } = new();
        public FeeRefreshJobResponse Status { get; set; } = new(Guid.NewGuid(), "Running", 0, request.Markets.Length, null);
    }
    private readonly object gate = new();
    private readonly Dictionary<Guid, Run> runs = [];
    private readonly Channel<Run> queue = Channel.CreateBounded<Run>(1);
    private bool stopping;
    public static bool Valid(RefreshFeesRequest request) => request.RuntimeSeconds is > 0 and <= 30 && request.Markets is { Length: > 0 and <= 10 } &&
        request.Markets.All(m => m is not null && m.Exchange is "Kalshi" or "Polymarket" && m.MarketId is { Length: > 0 and <= 256 } && !m.MarketId.Any(char.IsControl)) &&
        request.Markets.Distinct().Count() == request.Markets.Length;
    public FeeRefreshJobResponse Start(Guid actor, Guid workspace, RefreshFeesRequest request)
    {
        if (!Valid(request)) throw new ArgumentException("Invalid fee refresh bounds.");
        lock (gate)
        {
            if (stopping || runs.Values.Any(r => r.Status.State == "Running")) throw new InvalidOperationException("Fee refresh active.");
            while (runs.Count >= 4) { var first = runs.First(); runs.Remove(first.Key); first.Value.Cancel.Dispose(); }
            var run = new Run(actor, workspace, request with { Markets = request.Markets.ToArray() });
            if (!queue.Writer.TryWrite(run)) { run.Cancel.Dispose(); throw new InvalidOperationException("Fee refresh unavailable."); }
            runs.Add(run.Status.Id, run); return run.Status;
        }
    }
    private Run? Find(Guid workspace, Guid id) => runs.TryGetValue(id, out var run) && run.Workspace == workspace ? run : null;
    public FeeRefreshJobResponse? Status(Guid workspace, Guid id) { lock (gate) return Find(workspace, id)?.Status; }
    public FeeRefreshJobResponse? Cancel(Guid workspace, Guid id)
    { lock (gate) { var run = Find(workspace, id); if (run?.Status.State == "Running") run.Cancel.Cancel(); return run?.Status; } }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await foreach (var run in queue.Reader.ReadAllAsync(stoppingToken)) await RefreshAsync(run, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    private async Task RefreshAsync(Run run, CancellationToken stop)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(run.Request.RuntimeSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, budget.Token, run.Cancel.Token);
        var ct = linked.Token; var state = "Completed"; string? notice = null;
        try
        {
            foreach (var market in run.Request.Markets)
            {
                ct.ThrowIfCancellationRequested();
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RelationshipStore>().RequireMemberAsync(run.Actor, run.Workspace, true, ct);
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                if (!await db.CatalogMarkets.AnyAsync(m => m.Exchange == market.Exchange && m.NativeId == market.MarketId, ct))
                { state = "Partial"; notice = "Some markets were absent from the local catalog."; continue; }
                var store = scope.ServiceProvider.GetRequiredService<IFeeStore>();
                try
                {
                    var schedule = await scope.ServiceProvider.GetRequiredService<IPublicFeeSource>().ReadAsync(market.Exchange, market.MarketId, ct);
                    if (schedule.Exchange != market.Exchange || schedule.MarketId != market.MarketId) throw new ArgumentException("Mismatched fee market.");
                    await scope.ServiceProvider.GetRequiredService<RelationshipStore>().RequireMemberAsync(run.Actor, run.Workspace, true, ct);
                    db.AuditRecords.Add(new(run.Actor, run.Workspace, clock.GetUtcNow(), run.Status.Id.ToString(), "PublicFeeMetadataRefreshed",
                        System.Text.Json.JsonSerializer.Serialize(new { market.Exchange, market.MarketId, schedule.Fingerprint })));
                    await store.SaveAsync(schedule, ct);
                    scope.ServiceProvider.GetRequiredService<RealtimePublisher>().PaperValuationChanged(run.Workspace);
                    lock (gate) run.Status = run.Status with { CompletedMarkets = run.Status.CompletedMarkets + 1 };
                }
                catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or ArgumentException || e is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    await scope.ServiceProvider.GetRequiredService<RelationshipStore>().RequireMemberAsync(run.Actor, run.Workspace, true, ct);
                    // Replace a failed refresh's old bundle with an unavailable marker; never preserve an apparently verified stale success.
                    await store.SaveAsync(new(market.Exchange, market.MarketId, null, null, "Unknown", clock.GetUtcNow(), null,
                        "Public fee refresh failed", [], [], "Public metadata unavailable or invalid; explicitly retry."), ct);
                    scope.ServiceProvider.GetRequiredService<RealtimePublisher>().PaperValuationChanged(run.Workspace);
                    state = "Partial"; notice = "Some public fee metadata was unavailable or invalid. No credentials were used.";
                }
            }
        }
        catch (OperationCanceledException) { state = budget.IsCancellationRequested && !run.Cancel.IsCancellationRequested && !stop.IsCancellationRequested ? "Partial" : "Cancelled"; notice = "Fee refresh stopped within its explicit budget."; }
        catch (UnauthorizedAccessException) { state = "Cancelled"; notice = "Workspace access revoked."; }
        catch (Exception) { state = "Failed"; notice = "Fee refresh failed closed."; }
        lock (gate) run.Status = run.Status with { State = state, Notice = notice };
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        lock (gate) { stopping = true; foreach (var run in runs.Values.Where(r => r.Status.State == "Running")) run.Cancel.Cancel(); }
        await base.StopAsync(ct);
        lock (gate) foreach (var run in runs.Values.Where(r => r.Status.State == "Running")) run.Status = run.Status with { State = "Cancelled", Notice = "Backend stopped." };
    }
    public override void Dispose() { base.Dispose(); lock (gate) foreach (var run in runs.Values) run.Cancel.Dispose(); }
}
