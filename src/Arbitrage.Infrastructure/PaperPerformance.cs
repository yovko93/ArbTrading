using Arbitrage.Execution;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed record PaperPerformanceBucket(string Exchange, string Currency, decimal StartingCash, decimal CurrentCash, decimal OpenCostBasis,
    decimal SettledCostBasis, decimal SettlementPayout, decimal CumulativeRealizedPnl, int OpenPositionCount, int SettledPositionCount,
    int CommittedExecutionCount, int PartiallySettledExecutionCount, int SettledExecutionCount);
public sealed record PaperCurvePoint(DateTimeOffset Timestamp, string Exchange, string Currency, decimal CashAvailable, decimal CumulativeRealizedPnl,
    decimal RealizedPnlDelta, decimal RealizedPerformance, string Reason, Guid? ExecutionId, Guid? ResolutionId);
public sealed partial class PaperStore
{
    public async Task<(string Lifecycle, PaperPerformanceBucket[] Buckets)> PerformanceAsync(Guid workspace, Guid generationId, CancellationToken ct)
    {
        var generation = await GenerationAsync(workspace, generationId, ct) ?? throw new ArgumentException("GenerationNotFound");
        var balances = await BalancesAsync(generationId, ct);
        var positions = await db.Set<PaperPositionEntry>().AsNoTracking().Where(p => p.GenerationId == generationId).ToArrayAsync(ct);
        var executions = await db.Set<PaperExecutionEntry>().AsNoTracking().Where(e => e.GenerationId == generationId).ToArrayAsync(ct);
        var ids = executions.Select(e => e.Id).ToArray();
        var legs = await db.Set<PaperLegEntry>().AsNoTracking().Where(l => ids.Contains(l.ExecutionId)).ToArrayAsync(ct);
        return (generation.ClosedAt is null ? "Active" : positions.Any(p => p.Status == PaperPositionStatus.Open) ? "ClosedWithOpenPositions" : "FullySettled",
            balances.Select(b =>
            {
                var ps = positions.Where(p => p.Exchange == b.Exchange && p.Currency == b.Currency).ToArray();
                var es = executions.Where(e => legs.Any(l => l.ExecutionId == e.Id && l.Exchange == b.Exchange && l.Currency == b.Currency)).ToArray();
                return new PaperPerformanceBucket(b.Exchange, b.Currency, b.InitialCash, b.AvailableCash, ps.Where(p => p.Status == PaperPositionStatus.Open).Sum(p => p.CostBasis),
                    ps.Where(p => p.Status == PaperPositionStatus.Settled).Sum(p => p.CostBasis), ps.Sum(p => p.SettlementPayout ?? 0), ps.Sum(p => p.RealizedPnl ?? 0),
                    ps.Count(p => p.Status == PaperPositionStatus.Open), ps.Count(p => p.Status == PaperPositionStatus.Settled), es.Count(e => e.State == PaperExecutionState.Committed),
                    es.Count(e => e.State == PaperExecutionState.PartiallySettled), es.Count(e => e.State == PaperExecutionState.Settled));
            }).ToArray());
    }
    // Journal order is stable even when the test clock gives several commits the same UTC instant.
    // Generation revision provides the financial ordering; transaction rowid is SQLite's append order.
    public async Task<(PaperCurvePoint[] Points, bool HasMore)> CurveAsync(Guid workspace, Guid generation, string exchange, string currency, int page, int pageSize, CancellationToken ct)
    {
        if (await GenerationAsync(workspace, generation, ct) is null) throw new ArgumentException("GenerationNotFound");
        var balance = (await BalancesAsync(generation, ct)).SingleOrDefault(b => b.Exchange == exchange && b.Currency == currency);
        if (balance is null) return ([], false);
        var journals = await db.Set<PaperTransactionEntry>().FromSqlInterpolated($"SELECT * FROM PaperTransactionEntry WHERE GenerationId = {generation} ORDER BY CreatedAt, rowid").AsNoTracking().ToArrayAsync(ct);
        var ids = journals.Select(j => j.Id).ToArray();
        var entries = await db.Set<PaperLedgerEntry>().AsNoTracking().Where(e => ids.Contains(e.TransactionId) && e.Exchange == exchange && e.Currency == currency).ToArrayAsync(ct);
        var positions = await db.Set<PaperPositionEntry>().AsNoTracking().Where(p => p.GenerationId == generation && p.Exchange == exchange && p.Currency == currency && p.Status == PaperPositionStatus.Settled).ToArrayAsync(ct);
        decimal cash = 0, pnl = 0; var points = new List<PaperCurvePoint>();
        foreach (var j in journals)
        {
            var es = entries.Where(e => e.TransactionId == j.Id).ToArray();
            var ps = positions.Where(p => p.SettlementResolutionId == j.ResolutionId && j.ResolutionId != null).ToArray();
            if (es.Length == 0 && ps.Length == 0) continue;
            cash = checked(cash + es.Sum(e => e.AvailableDelta)); var delta = ps.Sum(p => p.RealizedPnl ?? 0); pnl = checked(pnl + delta);
            points.Add(new(j.CreatedAt, exchange, currency, cash, pnl, delta, checked(balance.InitialCash + pnl), j.Reason, j.ExecutionId, j.ResolutionId));
        }
        var skip = checked((page - 1) * pageSize);
        return (points.Skip(skip).Take(pageSize).ToArray(), points.Count > skip + pageSize);
    }
}
