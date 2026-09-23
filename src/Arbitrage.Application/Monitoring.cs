using Arbitrage.Domain;

namespace Arbitrage.Application;

public enum MonitoringState { Stopped, Starting, Running, Degraded, Stopping, Faulted }
public enum RankingLane { FeeAdjusted, GrossOnly, NearEdge, Blocked }
public enum MonitoringAlertSeverity { Opportunity }
public sealed record MonitoringProfile(int RelationshipLimit = 250, bool IncludeManualRelationships = false,
    decimal MinimumGrossEdge = .001m, decimal MinimumFeeAdjustedEdge = 0, decimal MaximumQuantity = 1000,
    int MaximumSkewMilliseconds = 1000, decimal NearEdgeWindow = .005m, string Sort = "default",
    bool EnableFeeAdjustedAlerts = true, bool EnableGrossOnlyAlerts = false,
    decimal FeeAlertEdge = .005m, decimal FeeAlertProfit = .01m, decimal GrossAlertEdge = .005m, decimal GrossAlertProfit = .01m,
    decimal RearmHysteresis = .001m, int CooldownSeconds = 60, bool CsvEnabled = false,
    int CsvIntervalSeconds = 30, int CsvTopRows = 100, int AlertRetentionCount = 5000, int AlertRetentionDays = 30)
{
    public static readonly string[] Sorts = ["default", "grossProfit", "grossEdge", "feeAdjustedProfit", "feeAdjustedEdge", "quantity", "nearEdgeDistance", "updated"];
    public bool Valid => RelationshipLimit is >= 1 and <= 1000 && MinimumGrossEdge is >= 0 and < 1 && MinimumFeeAdjustedEdge is >= 0 and < 1 &&
        MaximumQuantity is > 0 and <= 1_000_000_000m && MaximumSkewMilliseconds is >= 0 and <= 60000 && NearEdgeWindow is >= 0 and <= .1m &&
        Sorts.Contains(Sort, StringComparer.Ordinal) && FeeAlertEdge is >= 0 and < 1 && GrossAlertEdge is >= 0 and < 1 &&
        FeeAlertProfit is >= 0 and <= 1_000_000_000m && GrossAlertProfit is >= 0 and <= 1_000_000_000m && RearmHysteresis >= 0 &&
        (FeeAlertEdge == 0 ? RearmHysteresis == 0 : RearmHysteresis < FeeAlertEdge) && (GrossAlertEdge == 0 ? RearmHysteresis == 0 : RearmHysteresis < GrossAlertEdge) &&
        CooldownSeconds is >= 0 and <= 86400 && CsvIntervalSeconds is >= 5 and <= 3600 && CsvTopRows is >= 1 and <= 100 &&
        AlertRetentionCount is >= 1 and <= 5000 && AlertRetentionDays is >= 1 and <= 30;
}
public sealed record MonitoredOpportunity(ArbitrageOpportunitySnapshot Result, long EvaluationGeneration, RankingLane Lane,
    decimal? BestEdge, decimal? RequiredEdge, decimal? Distance, decimal AvailableQuantity, string AlertState = "Armed")
{ public long ProfileVersion { get; init; } }
public sealed record MonitoringAlert(Guid AlertId, Guid WorkspaceId, string OpportunityKey, DateTimeOffset TriggeredAt,
    RankingLane Lane, string Reason, MonitoredOpportunity Opportunity)
{ public MonitoringAlertSeverity Severity { get; init; } = MonitoringAlertSeverity.Opportunity; }
public sealed record MonitoringCoverage(int ApprovedRelationshipsAvailable, int RelationshipsMonitored, int RelationshipsSkippedByBound,
    bool CoveragePartial, int PlansBuilt, int PlansWithBooksAvailable, int PlansWithActionableBooks, int PlansWithResolvedFees,
    int FeeAdjustedOpportunities, int GrossOnlyOpportunities, int NearEdgeCandidates, int BlockedCandidates);
public static class MonitoringRanking
{
    public static MonitoredOpportunity Classify(ArbitrageOpportunitySnapshot r, long generation, MonitoringProfile profile)
    {
        var fee = r.Fees;
        var trustworthy = r.RelationshipEligible && r.BooksActionable && r.SkewAcceptable && r.Status is OpportunityStatus.Detected or OpportunityStatus.NoGrossEdge;
        if (!trustworthy) return new(r, generation, RankingLane.Blocked, null, null, null, 0);
        if (r.GrossArbitrageExists && fee is { State: FeeOpportunityStatus.FeeAdjustedDetected, TotalExchangeFees: not null, FeeAdjustedGuaranteedProfit: > 0 })
            return new(r, generation, RankingLane.FeeAdjusted, fee.FeeAdjustedEdgePerShare, profile.MinimumFeeAdjustedEdge, null, r.PairedQuantity);
        if (r.GrossArbitrageExists && (fee is null || fee.TotalExchangeFees is null))
            return new(r, generation, RankingLane.GrossOnly, r.GrossEdgePerShare, profile.MinimumGrossEdge, null, r.PairedQuantity);
        var edge = fee is { State: FeeOpportunityStatus.FeeAdjustedNoEdge, TotalExchangeFees: not null } ? fee.FeeAdjustedEdgePerShare : r.BestObservedGrossEdge;
        var threshold = fee is { State: FeeOpportunityStatus.FeeAdjustedNoEdge, TotalExchangeFees: not null } ? profile.MinimumFeeAdjustedEdge : profile.MinimumGrossEdge;
        var distance = edge is { } e ? threshold - e : (decimal?)null;
        var quantity = r.PairedQuantity > 0 ? r.PairedQuantity : r.BestObservedQuantity;
        return new(r, generation, distance is >= 0 && distance <= profile.NearEdgeWindow && quantity > 0 ? RankingLane.NearEdge : RankingLane.Blocked,
            edge, threshold, distance, quantity);
    }
    // Explicit quality tie-break, not an economic score.
    private static int Quality(OpportunityInputQuality quality) => quality switch
    { OpportunityInputQuality.RealtimeContinuous => 0, OpportunityInputQuality.FreshRest => 1, OpportunityInputQuality.Mixed => 2, OpportunityInputQuality.RealtimeBestEffort => 3, _ => 4 };
    public static IEnumerable<MonitoredOpportunity> Sort(IEnumerable<MonitoredOpportunity> items, string sort)
    {
        if (!MonitoringProfile.Sorts.Contains(sort, StringComparer.Ordinal)) throw new ArgumentException("Invalid ranking sort.");
        IOrderedEnumerable<MonitoredOpportunity> Order(IEnumerable<MonitoredOpportunity> lane) => sort switch
        {
            "grossProfit" => lane.OrderByDescending(x => x.Result.GrossProfit),
            "grossEdge" => lane.OrderByDescending(x => x.Result.GrossEdgePerShare),
            "feeAdjustedProfit" => lane.OrderByDescending(x => x.Result.Fees?.FeeAdjustedGuaranteedProfit),
            "feeAdjustedEdge" => lane.OrderByDescending(x => x.Result.Fees?.FeeAdjustedEdgePerShare),
            "quantity" => lane.OrderByDescending(x => x.AvailableQuantity),
            "nearEdgeDistance" => lane.OrderBy(x => x.Distance is null).ThenBy(x => x.Distance),
            "updated" => lane.OrderByDescending(x => x.Result.EvaluatedAt),
            _ => lane.OrderByDescending(x => x.Lane == RankingLane.FeeAdjusted ? x.Result.Fees?.FeeAdjustedGuaranteedProfit : x.Lane == RankingLane.GrossOnly ? x.Result.GrossProfit : null)
                .ThenByDescending(x => x.Lane == RankingLane.FeeAdjusted ? x.Result.Fees?.FeeAdjustedEdgePerShare : x.Lane == RankingLane.GrossOnly ? x.Result.GrossEdgePerShare : null)
                .ThenBy(x => x.Lane == RankingLane.NearEdge ? x.Distance : null).ThenByDescending(x => x.AvailableQuantity)
                .ThenBy(x => x.Lane == RankingLane.NearEdge ? 0 : Quality(x.Result.InputQuality)).ThenBy(x => x.Result.ObservedSkew)
        };
        return items.GroupBy(x => x.Lane).OrderBy(g => g.Key).SelectMany(g => Order(g).ThenBy(x => x.Result.OpportunityKey, StringComparer.Ordinal));
    }
    public static ArbitrageOpportunitySnapshot Compact(ArbitrageOpportunitySnapshot r) => r with
    {
        Segments = [], Legs = [.. r.Legs.Select(l => l with { LiquiditySources = [] })],
        Fees = r.Fees is { } f ? f with { Breakdown = [.. f.Breakdown.GroupBy(q => (q.Context.Exchange, q.Context.MarketId)).Select(g => g.First())] } : null,
        Warnings = ["Monitoring summary omits depth and per-level fee detail. Use explicit evaluation for a full breakdown."]
    };
}
public sealed class OpportunityAlertState
{
    public bool Armed { get; private set; } = true;
    public bool PreviouslyQualified { get; private set; }
    public DateTimeOffset? LastAlertAt { get; private set; }
    public decimal? LastAlertMetric { get; private set; }
    public DateTimeOffset? LastQualifiedAt { get; private set; }
    public DateTimeOffset? LastUnqualifiedAt { get; private set; }
    public string State { get; private set; } = "Armed";
    public void RestoreCooldown(DateTimeOffset last) => LastAlertAt = last;
    public bool Observe(decimal? edge, decimal? profit, bool valid, decimal threshold, decimal profitThreshold, decimal hysteresis, TimeSpan cooldown, DateTimeOffset now, bool eligibleLane = true)
    {
        var qualified = eligibleLane && valid && edge is { } e && e > 0 && e >= threshold && profit is { } p && p > 0 && p >= profitThreshold;
        if (qualified) LastQualifiedAt = now; else LastUnqualifiedAt = now;
        if (valid && edge is { } below && (threshold == 0 ? below <= 0 : below < threshold - hysteresis)) { Armed = true; State = "Armed"; }
        var crossing = qualified && !PreviouslyQualified; PreviouslyQualified = qualified;
        if (!crossing) { if (qualified && !Armed) State = "NotRearmed"; return false; }
        if (!Armed) { State = "NotRearmed"; return false; }
        Armed = false; // A cooldown-suppressed crossing is consumed; another drop/rearm/cross is required.
        if (LastAlertAt is { } last && now - last < cooldown) { State = "Cooldown"; return false; }
        LastAlertAt = now; LastAlertMetric = edge; State = "Triggered"; return true;
    }
}

public enum LocalChangeKind { Instrument, Market, Relationship, Profile }
public sealed record LocalInputChange(LocalChangeKind Kind, string? Exchange = null, string? MarketId = null, string? InstrumentId = null, Guid? WorkspaceId = null, Guid? RelationshipId = null);
// Nonblocking producers, no callbacks or I/O on exchange receive loops. Overflow is explicitly reconciled.
public sealed class LocalInputChanges(int capacity = 2048)
{
    private readonly object gate = new();
    private readonly HashSet<LocalInputChange> pending = [];
    public long Received { get; private set; }
    public long Coalesced { get; private set; }
    public long Dropped { get; private set; }
    public bool ReconciliationRequired { get; private set; }
    public int Count { get { lock (gate) return pending.Count; } }
    public void Publish(LocalInputChange change)
    {
        lock (gate)
        {
            Received++; if (pending.Contains(change)) { Coalesced++; return; }
            if (pending.Count >= capacity) { Dropped++; ReconciliationRequired = true; return; }
            pending.Add(change);
        }
    }
    public (LocalInputChange[] Changes, bool Overflow) Drain()
    { lock (gate) { var result = (pending.ToArray(), ReconciliationRequired); pending.Clear(); ReconciliationRequired = false; return result; } }
}
public sealed class MonitoringDependencies
{
    private readonly Dictionary<(string, string, string), HashSet<string>> instruments = [];
    private readonly Dictionary<(string, string), HashSet<string>> markets = [];
    private readonly Dictionary<Guid, HashSet<string>> relationships = [];
    public int Count { get; private set; }
    public void Add(string key, OpportunityPlan plan)
    {
        if (++Count > 2000) throw new ArgumentException("Monitoring plan bound exceeded.");
        foreach (var id in new[] { plan.A, plan.B }) { Add(instruments, (id.Exchange, id.NativeMarketId, id.NativeInstrumentId), key); Add(markets, (id.Exchange, id.NativeMarketId), key); }
        Add(relationships, plan.Relationship.Id, key);
    }
    private static void Add<T>(Dictionary<T, HashSet<string>> map, T id, string key) where T : notnull
    { if (!map.TryGetValue(id, out var set)) map[id] = set = new(StringComparer.Ordinal); set.Add(key); }
    public IEnumerable<string> Affected(LocalInputChange change) => change.Kind switch
    {
        LocalChangeKind.Instrument => instruments.GetValueOrDefault((change.Exchange!, change.MarketId!, change.InstrumentId!)) ?? [],
        LocalChangeKind.Market => markets.GetValueOrDefault((change.Exchange!, change.MarketId!)) ?? [],
        LocalChangeKind.Relationship => relationships.GetValueOrDefault(change.RelationshipId ?? Guid.Empty) ?? [],
        _ => []
    };
}
