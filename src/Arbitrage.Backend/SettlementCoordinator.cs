using Arbitrage.Contracts;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public sealed record ResolutionTicket(Guid Id, Guid Actor, Guid Workspace, DateTimeOffset CreatedAt, ResolutionSelection Selection, SettlementProjection Projection);
public sealed class SettlementMemory(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, ResolutionTicket> previews = [];
    private readonly Dictionary<Guid, long[]> counters = [];
    public void Add(ResolutionTicket ticket)
    {
        lock (gate)
        {
            foreach (var old in previews.Values.Where(t => clock.GetUtcNow() - t.CreatedAt > PaperSettlement.PreviewLifetime).ToArray()) previews.Remove(old.Id);
            if (previews.Count >= 256) previews.Remove(previews.Values.MinBy(t => t.CreatedAt)!.Id);
            previews[ticket.Id] = ticket;
        }
    }
    public ResolutionTicket? Find(Guid id, Guid actor, Guid workspace)
    { lock (gate) return previews.GetValueOrDefault(id) is { } t && t.Actor == actor && t.Workspace == workspace ? t : null; }
    public void Count(Guid workspace, int index, int amount = 1)
    {
        lock (gate)
        {
            if (!counters.TryGetValue(workspace, out var values)) { if (counters.Count >= 128) return; counters[workspace] = values = new long[8]; }
            values[index] = Math.Min(long.MaxValue - amount, values[index]) + amount;
        }
    }
    public PaperSettlementDiagnosticsResponse Read(Guid workspace)
    { lock (gate) { var c = counters.GetValueOrDefault(workspace) ?? new long[8]; return new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7]); } }
}
public sealed class SettlementCoordinator(PaperStore store, SettlementMemory memory, TimeProvider clock, LocalOptions options)
{
    public static ResolutionSelection Selection(PaperResolutionSelection s) => new(s.GenerationId, s.Exchange, s.MarketId, s.WinningInstrumentId);
    public async Task<PaperResolutionPreviewResponse> PreviewAsync(Guid actor, Guid workspace, PaperResolutionSelection s, CancellationToken ct)
    {
        var at = clock.GetUtcNow(); var id = Guid.NewGuid(); memory.Count(workspace, 0);
        var p = options.TradingMode == "Paper" ? await store.ResolutionPreviewAsync(actor, workspace, Selection(s), ct) :
            new SettlementProjection(SettlementRejection.NotPaperMode, "", null, [], [], [], []);
        if (p.Rejection == SettlementRejection.None) memory.Add(new(id, actor, workspace, at, Selection(s), p));
        return new(id, at, at + PaperSettlement.PreviewLifetime, s, p.Rejection == SettlementRejection.None, p.Rejection.ToString(),
            p.Outcomes.Select(o => new PaperPayoutResponse(o.InstrumentId, o.Outcome, o.PayoutPerShare)).ToArray(),
            p.Positions.Select(v => new PaperPositionSettlementResponse(v.PositionId, v.InstrumentId, v.Outcome, v.Currency, v.Quantity, v.CostBasis, v.Payout, v.RealizedPnl)).ToArray(),
            p.Executions.Select(e => new PaperExecutionSettlementEffectResponse(e.ExecutionId, SettlementEndpoints.Economics(e.Economics))).ToArray(),
            p.Credits.Select(c => new PaperSettlementCreditResponse(c.Exchange, c.Currency, c.Payout, c.AvailableBefore, c.AvailableAfter)).ToArray(),
            ["MANUAL PAPER SCENARIO — not evidence of the real market result.", "PAPER SIMULATION ONLY. The confirmed resolution is immutable in this generation. Use another generation to test another outcome.",
                "Payout stays in the recorded venue/currency bucket. No conversion, redemption, transfer or market valuation is performed."]);
    }
    public async Task<ResolutionCommitResult> ConfirmAsync(Guid actor, Guid workspace, ConfirmResolutionRequest request, string correlation, CancellationToken ct)
    {
        if (options.TradingMode != "Paper") return new(SettlementRejection.NotPaperMode);
        if (!request.ConfirmSimulation || request.RequestId == Guid.Empty) return new(SettlementRejection.ConfirmationRequired);
        var input = new ResolutionConfirmation(request.RequestId, request.PreviewId, Selection(request.Selection), request.ConfirmSimulation);
        var ticket = memory.Find(request.PreviewId, actor, workspace);
        // Durable duplicates remain available after restart and expiry. Store reauthorizes under its transaction.
        var prior = await store.ResolutionRequestAsync(workspace, request.RequestId, ct);
        if (prior is null && ticket is null) return new(SettlementRejection.PreviewExpired);
        if (prior is null && ticket!.Selection != input.Selection) return new(SettlementRejection.PreviewChanged);
        var result = await store.CommitResolutionAsync(actor, workspace, input, ticket?.Projection.Fingerprint ?? "", ticket?.CreatedAt ?? default, correlation, ct);
        if (result.Duplicate) memory.Count(workspace, 7);
        else if (result.Resolution is not null)
        {
            memory.Count(workspace, 1); memory.Count(workspace, 3, ticket!.Projection.Positions.Length);
            memory.Count(workspace, 4, ticket.Projection.Executions.Count(e => e.Economics.State == PaperExecutionState.Settled));
            memory.Count(workspace, 5, ticket.Projection.Executions.Count(e => e.Economics.State == PaperExecutionState.PartiallySettled));
        }
        else if (result.Rejection == SettlementRejection.IntegrityFailure) memory.Count(workspace, 6);
        else memory.Count(workspace, 2);
        return result;
    }
}
