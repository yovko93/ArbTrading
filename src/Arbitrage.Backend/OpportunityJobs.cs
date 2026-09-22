using System.Threading.Channels;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Strategies;

namespace Arbitrage.Backend;

// Ephemeral, globally single-flight, at most four retained runs with fifty results each.
public sealed class OpportunityJobs(IServiceScopeFactory scopes, TimeProvider clock) : BackgroundService
{
    private sealed class Run(Guid actor, Guid workspace, EvaluateOpportunitiesRequest request, DateTimeOffset now)
    {
        public Guid Actor { get; } = actor;
        public Guid Workspace { get; } = workspace;
        public EvaluateOpportunitiesRequest Request { get; } = request;
        public CancellationTokenSource Cancel { get; } = new();
        public OpportunityJobResponse Status { get; set; } = new(Guid.NewGuid(), "Running", 0, 0, now, null, null);
        public List<ArbitrageOpportunitySnapshot> Results { get; } = [];
    }
    private readonly object gate = new();
    private readonly Dictionary<Guid, Run> runs = [];
    private readonly Channel<Run> queue = Channel.CreateBounded<Run>(1);
    private bool stopping;
    public static bool Valid(EvaluateOpportunitiesRequest r) => r.MaximumRelationshipsPerRun is > 0 and <= 500 &&
        r.MaximumOpportunitiesReturned is > 0 and <= 50 && r.RuntimeSeconds is > 0 and <= 30 && r.RelationshipId != Guid.Empty &&
        r.Exchange is null or "Kalshi" or "Polymarket" && r.TargetExchange is null or "Kalshi" or "Polymarket" && Settings(r).Valid;
    private static OpportunitySettings Settings(EvaluateOpportunitiesRequest r) => new(r.MinimumGrossEdgePerShare, r.MaximumEvaluationQuantity,
        r.MaximumEvaluationNotional, r.MaximumSkewMilliseconds, r.RequestedQuantity);
    public OpportunityJobResponse Start(Guid actor, Guid workspace, EvaluateOpportunitiesRequest request)
    {
        if (!Valid(request)) throw new ArgumentException("Invalid evaluation bounds.");
        lock (gate)
        {
            if (stopping || runs.Values.Any(r => r.Status.State == "Running")) throw new InvalidOperationException("Evaluation already active.");
            while (runs.Count >= 4)
            {
                var old = runs.Values.OrderBy(r => r.Status.StartedAt).First(); runs.Remove(old.Status.Id); old.Cancel.Dispose();
            }
            var run = new Run(actor, workspace, request, clock.GetUtcNow());
            if (!queue.Writer.TryWrite(run)) { run.Cancel.Dispose(); throw new InvalidOperationException("Evaluation unavailable."); }
            runs.Add(run.Status.Id, run); return run.Status;
        }
    }
    public OpportunityJobResponse? Status(Guid workspace, Guid id) { lock (gate) return Find(workspace, id)?.Status; }
    public OpportunityJobResponse? Cancel(Guid workspace, Guid id)
    {
        lock (gate) { var run = Find(workspace, id); if (run?.Status.State == "Running") run.Cancel.Cancel(); return run?.Status; }
    }
    private Run? Find(Guid workspace, Guid id) => runs.TryGetValue(id, out var run) && run.Workspace == workspace ? run : null;
    public async Task<OpportunityPageResponse?> PageAsync(Guid actor, Guid workspace, Guid id, int page, int size, bool diagnostics,
        bool sortProfit, OpportunityCoordinator coordinator, CancellationToken ct)
    {
        ArbitrageOpportunitySnapshot[] snapshots; OpportunityJobResponse status; bool manual;
        lock (gate)
        {
            var run = Find(workspace, id); if (run is null) return null;
            snapshots = run.Results.ToArray(); status = run.Status; manual = run.Request.IncludeManualRelationships;
        }
        var validated = new List<ArbitrageOpportunitySnapshot>();
        foreach (var snapshot in snapshots) validated.Add(await coordinator.ValidateAsync(actor, workspace, snapshot, manual, false, ct));
        var filtered = validated.Where(r => diagnostics || r.GrossArbitrageExists);
        var ordered = sortProfit ? filtered.OrderByDescending(r => r.GrossArbitrageExists).ThenByDescending(r => r.GrossProfit).ThenBy(r => r.OpportunityKey) : filtered.OrderBy(r => r.OpportunityKey);
        var items = ordered.ToArray();
        return new(items.Skip((page - 1) * size).Take(size).Select(OpportunityEndpoints.Map).ToArray(), items.Length, page, size, status);
    }
    public async Task<OpportunityResponse?> CurrentAsync(Guid actor, Guid workspace, string key, OpportunityCoordinator coordinator, CancellationToken ct)
    {
        ArbitrageOpportunitySnapshot? snapshot; bool manual;
        lock (gate)
        {
            var run = runs.Values.Where(r => r.Workspace == workspace && r.Results.Any(s => s.OpportunityKey == key)).OrderByDescending(r => r.Status.StartedAt).FirstOrDefault();
            snapshot = run?.Results.FirstOrDefault(s => s.OpportunityKey == key); manual = run?.Request.IncludeManualRelationships ?? false;
        }
        return snapshot is null ? null : OpportunityEndpoints.Map(await coordinator.ValidateAsync(actor, workspace, snapshot, manual, false, ct));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await foreach (var run in queue.Reader.ReadAllAsync(stoppingToken)) await EvaluateAsync(run, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    private async Task EvaluateAsync(Run run, CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(run.Request.RuntimeSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, run.Cancel.Token, timeout.Token);
        var ct = linked.Token; var state = "Completed"; string? notice = null;
        try
        {
            using var scope = scopes.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<IRelationshipProvider>();
            var coordinator = scope.ServiceProvider.GetRequiredService<OpportunityCoordinator>();
            var request = run.Request;
            var page = await provider.ReadEvaluationPageAsync(run.Actor, run.Workspace, request.IncludeManualRelationships, request.RelationshipId,
                request.Exchange, 0, request.MaximumRelationshipsPerRun, ct);
            lock (gate) run.Status = run.Status with { RelationshipsScanned = page.Scanned };
            if (page.HasMore) { state = "Partial"; notice = "Relationship bound reached."; }
            var retainedSegments = 0;
            var plans = page.Items.Where(r => request.TargetExchange is null ||
                (request.Exchange is null ? r.Source.Exchange == request.TargetExchange || r.Target.Exchange == request.TargetExchange :
                r.Source.Exchange == request.Exchange && r.Target.Exchange == request.TargetExchange || r.Target.Exchange == request.Exchange && r.Source.Exchange == request.TargetExchange))
                .SelectMany(OpportunityPlanner.Plan);
            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                lock (gate) if (run.Results.Count >= request.MaximumOpportunitiesReturned) { state = "Partial"; notice = "Result bound reached."; break; }
                var result = await coordinator.EvaluateAsync(run.Actor, run.Workspace, plan, Settings(request), request.IncludeManualRelationships, ct);
                ct.ThrowIfCancellationRequested();
                if (retainedSegments + result.Segments.Length > 100_000) { state = "Partial"; notice = "Retained depth memory bound reached."; break; }
                retainedSegments += result.Segments.Length;
                lock (gate) { run.Results.Add(result); run.Status = run.Status with { Results = run.Results.Count }; }
            }
            if (run.Results.Count == 0) notice ??= "No eligible mapped relationships in this bounded scope. Manual trust requires explicit opt-in.";
        }
        catch (OperationCanceledException) { state = timeout.IsCancellationRequested && !run.Cancel.IsCancellationRequested && !stoppingToken.IsCancellationRequested ? "Partial" : "Cancelled"; notice = state == "Partial" ? "Runtime budget reached." : "Evaluation cancelled."; }
        catch (UnauthorizedAccessException) { state = "Cancelled"; notice = "Workspace access revoked."; lock (gate) run.Results.Clear(); }
        catch (Exception) { state = "Failed"; notice = "Evaluation failed closed; retry explicitly after checking inputs."; lock (gate) run.Results.Clear(); }
        lock (gate) run.Status = run.Status with { State = state, Results = run.Results.Count, EndedAt = clock.GetUtcNow(), Notice = notice };
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) { stopping = true; foreach (var run in runs.Values.Where(r => r.Status.State == "Running")) run.Cancel.Cancel(); }
        await base.StopAsync(cancellationToken);
        lock (gate) foreach (var run in runs.Values.Where(r => r.Status.State == "Running"))
            run.Status = run.Status with { State = "Cancelled", EndedAt = clock.GetUtcNow(), Notice = "Backend stopped before evaluation completed." };
    }
    public override void Dispose() { base.Dispose(); lock (gate) foreach (var run in runs.Values) run.Cancel.Dispose(); }
}
