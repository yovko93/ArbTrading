using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Arbitrage.Execution;

public sealed record PaperRiskLimits(decimal MinimumCashReserveFraction, decimal MaximumSingleExecutionDebitFraction,
    decimal MaximumOpenCostBasisFraction, decimal MaximumMarketCostBasisFraction, decimal MaximumInstrumentCostBasisFraction,
    int MaximumOpenPositions, int MaximumOpenExecutions, int MaximumOpenExecutionsPerRelationship,
    decimal MaximumRequestedQuantity, decimal MinimumFeeAdjustedEdgePerShare, decimal MinimumFeeAdjustedProfit)
{
    public bool IsValid => MinimumCashReserveFraction is >= 0 and < 1 && MaximumSingleExecutionDebitFraction is > 0 and <= 1 &&
        MaximumOpenCostBasisFraction is > 0 and <= 1 && MaximumMarketCostBasisFraction > 0 && MaximumMarketCostBasisFraction <= MaximumOpenCostBasisFraction &&
        MaximumInstrumentCostBasisFraction > 0 && MaximumInstrumentCostBasisFraction <= MaximumMarketCostBasisFraction &&
        MaximumOpenPositions is >= 1 and <= 1000 && MaximumOpenExecutions is >= 1 and <= 1000 &&
        MaximumOpenExecutionsPerRelationship >= 1 && MaximumOpenExecutionsPerRelationship <= MaximumOpenExecutions &&
        MaximumRequestedQuantity is > 0 and <= PaperPlanner.MaximumQuantity &&
        MinimumFeeAdjustedEdgePerShare is >= PaperPlanner.MinimumFeeAdjustedEdge and < 1 && MinimumFeeAdjustedProfit >= 0;
    // Explicit field order and invariant canonical decimals; independent of JSON settings and locale.
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("1|" + string.Join("|",
        new decimal[] { MinimumCashReserveFraction, MaximumSingleExecutionDebitFraction, MaximumOpenCostBasisFraction,
            MaximumMarketCostBasisFraction, MaximumInstrumentCostBasisFraction, MaximumOpenPositions, MaximumOpenExecutions,
            MaximumOpenExecutionsPerRelationship, MaximumRequestedQuantity, MinimumFeeAdjustedEdgePerShare, MinimumFeeAdjustedProfit }
        .Select(x => x.ToString("G29", CultureInfo.InvariantCulture))))));
}
public sealed record PaperRiskProfile(int PolicyVersion, Guid Revision, PaperRiskLimits Limits, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, Guid UpdatedBy, string Fingerprint)
{
    public const int Version = 1;
    public bool IsValid => PolicyVersion == Version && Revision != Guid.Empty && UpdatedBy != Guid.Empty &&
        CreatedAt <= UpdatedAt && Limits is { IsValid: true } && Fingerprint == Limits.Fingerprint;
}
public enum PaperRiskOutcome { Approved, Rejected, NotConfigured, InvalidFinancialState }
public enum PaperRiskViolationCode { RiskPolicyNotConfigured, RequestedQuantityLimit, MinimumCashReserve, SingleExecutionDebitLimit,
    TotalOpenCostBasisLimit, MarketCostBasisLimit, InstrumentCostBasisLimit, OpenPositionCountLimit, OpenExecutionCountLimit,
    RelationshipExecutionCountLimit, MinimumEntryEdge, MinimumEntryProfit, FinancialStateChanged, RiskPolicyChanged, ArithmeticOverflow, IntegrityFailure }
public sealed record PaperRiskViolation(PaperRiskViolationCode Code, string? Exchange = null, string? Currency = null,
    string? MarketId = null, string? InstrumentId = null, decimal? CurrentValue = null, decimal? ProposedDelta = null,
    decimal? ProjectedValue = null, decimal? LimitValue = null, decimal? RemainingHeadroom = null);
public sealed record PaperRiskBucket(string Exchange, string Currency, decimal InitialCash, decimal AvailableCash, long Revision);
public sealed record PaperRiskPosition(string Exchange, string Currency, string MarketId, string InstrumentId, string Outcome, decimal CostBasis);
public sealed record PaperRiskOpenExecution(Guid RelationshipId);
public sealed record CurrentPaperRiskState(Guid? GenerationId, long FinancialRevision, bool Healthy,
    ImmutableArray<PaperRiskBucket> Buckets, ImmutableArray<PaperRiskPosition> Positions, ImmutableArray<PaperRiskOpenExecution> Executions);
public sealed record PaperRiskBucketAssessment(string Exchange, string Currency, decimal InitialCash, decimal AvailableCash,
    decimal ProposedDebit, decimal ProjectedAvailableCash, decimal RequiredCashReserve, decimal CashReserveHeadroom,
    decimal CurrentOpenCostBasis, decimal ProjectedOpenCostBasis, decimal MaximumOpenCostBasis, decimal OpenCostHeadroom,
    decimal LargestMarketCostBasis, decimal MaximumMarketCostBasis, decimal LargestInstrumentCostBasis, decimal MaximumInstrumentCostBasis);
public sealed record PaperRiskExposure(string Exchange, string Currency, string MarketId, string? InstrumentId,
    decimal CurrentValue, decimal ProposedDelta, decimal ProjectedValue, decimal LimitValue, decimal RemainingHeadroom);
public sealed record PaperRiskRelationship(Guid RelationshipId, int OpenExecutionCount, int MaximumOpenExecutionsPerRelationship);
public sealed record PaperRiskDecision(PaperRiskOutcome Decision, int? PolicyVersion, Guid? PolicyRevision, string? PolicyFingerprint,
    Guid? GenerationId, long FinancialRevision, ImmutableArray<PaperRiskViolation> Violations,
    ImmutableArray<PaperRiskBucketAssessment> BucketAssessments, ImmutableArray<PaperRiskExposure> Exposures,
    int CurrentOpenPositions, int ProjectedOpenPositions, int CurrentOpenExecutions, int ProjectedOpenExecutions,
    int CurrentRelationshipOpenExecutions, int ProjectedRelationshipOpenExecutions,
    ImmutableArray<PaperRiskRelationship> Relationships, DateTimeOffset EvaluatedAt);
public sealed record PaperRiskProof(PaperRiskDecision Decision, Guid PlanId, decimal Quantity, decimal Cost);

// Pure accounting admission. No acquisition, marks, fee formulas, or opportunity ranking.
public static class PaperRiskEvaluator
{
    public static PaperRiskDecision Evaluate(PaperRiskProfile? profile, CurrentPaperRiskState state, PaperPlan? plan, DateTimeOffset at)
    {
        var violations = ImmutableArray.CreateBuilder<PaperRiskViolation>();
        var buckets = ImmutableArray.CreateBuilder<PaperRiskBucketAssessment>();
        var exposures = ImmutableArray.CreateBuilder<PaperRiskExposure>();
        var relationships = ImmutableArray.CreateBuilder<PaperRiskRelationship>();
        int positions = state.Positions.Length, executions = state.Executions.Length, related = 0, projectedPositions = positions;
        var added = plan is null ? 0 : 1;
        PaperRiskDecision Result(PaperRiskOutcome outcome) => new(outcome, profile?.PolicyVersion, profile?.Revision, profile?.Fingerprint,
            state.GenerationId, state.FinancialRevision, violations.ToImmutable(), buckets.ToImmutable(), exposures.ToImmutable(),
            positions, projectedPositions, executions, executions + added, related, related + added, relationships.ToImmutable(), at);
        if (profile is null) { violations.Add(new(PaperRiskViolationCode.RiskPolicyNotConfigured)); return Result(PaperRiskOutcome.NotConfigured); }
        if (!profile.IsValid || !state.Healthy) { violations.Add(new(PaperRiskViolationCode.IntegrityFailure)); return Result(PaperRiskOutcome.InvalidFinancialState); }
        var l = profile.Limits;
        void Check(PaperRiskViolationCode code, decimal current, decimal delta, decimal limit, string? exchange = null,
            string? currency = null, string? market = null, string? instrument = null, bool minimum = false)
        {
            var projected = checked(current + delta); var headroom = minimum ? checked(projected - limit) : checked(limit - projected);
            if (headroom < 0) violations.Add(new(code, exchange, currency, market, instrument, current, delta, projected, limit, headroom));
        }
        try
        {
            if (state.Buckets.Any(b => b.InitialCash < 0 || b.AvailableCash < 0) || state.Positions.Any(p => p.CostBasis < 0) ||
                state.Buckets.Select(b => (b.Exchange, b.Currency)).Distinct().Count() != state.Buckets.Length ||
                state.Positions.Any(p => !state.Buckets.Any(b => b.Exchange == p.Exchange && b.Currency == p.Currency)) ||
                plan is not null && (plan.Debits.Any(d => d.Total < 0 || !state.Buckets.Any(b => b.Exchange == d.Exchange && b.Currency == d.Currency)) ||
                    plan.Fills.Any(f => f.Notional < 0 || f.Fee < 0)))
            { violations.Add(new(PaperRiskViolationCode.IntegrityFailure)); return Result(PaperRiskOutcome.InvalidFinancialState); }
            var proposed = plan?.Fills.GroupBy(f => (f.Instrument.Exchange, f.Currency, MarketId: f.Instrument.NativeMarketId,
                InstrumentId: f.Instrument.NativeInstrumentId, f.Instrument.Outcome)).Select(g =>
                new PaperRiskPosition(g.Key.Exchange, g.Key.Currency, g.Key.MarketId, g.Key.InstrumentId, g.Key.Outcome, g.Sum(f => checked(f.Notional + f.Fee)))).ToArray() ?? [];
            projectedPositions += proposed.Count(p => !state.Positions.Any(x => x.Exchange == p.Exchange && x.Currency == p.Currency &&
                x.MarketId == p.MarketId && x.InstrumentId == p.InstrumentId && x.Outcome == p.Outcome));
            Check(PaperRiskViolationCode.OpenPositionCountLimit, positions, projectedPositions - positions, l.MaximumOpenPositions);
            Check(PaperRiskViolationCode.OpenExecutionCountLimit, executions, added, l.MaximumOpenExecutions);
            var counts = state.Executions.GroupBy(e => e.RelationshipId).ToDictionary(g => g.Key, g => g.Count());
            if (plan is not null) { related = counts.GetValueOrDefault(plan.Proof.RelationshipId); counts.TryAdd(plan.Proof.RelationshipId, 0); }
            foreach (var r in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
            {
                Check(PaperRiskViolationCode.RelationshipExecutionCountLimit, r.Value, plan?.Proof.RelationshipId == r.Key ? 1 : 0, l.MaximumOpenExecutionsPerRelationship);
                if (relationships.Count < 20) relationships.Add(new(r.Key, r.Value, l.MaximumOpenExecutionsPerRelationship));
            }
            foreach (var b in state.Buckets)
            {
                var current = state.Positions.Where(p => p.Exchange == b.Exchange && p.Currency == b.Currency).ToArray();
                var next = proposed.Where(p => p.Exchange == b.Exchange && p.Currency == b.Currency).ToArray();
                var debit = plan?.Debits.Where(d => d.Exchange == b.Exchange && d.Currency == b.Currency).Sum(d => d.Total) ?? 0;
                if (debit != next.Sum(p => p.CostBasis)) { violations.Add(new(PaperRiskViolationCode.IntegrityFailure)); return Result(PaperRiskOutcome.InvalidFinancialState); }
                var reserve = checked(b.InitialCash * l.MinimumCashReserveFraction);
                var cost = current.Sum(p => p.CostBasis); var delta = next.Sum(p => p.CostBasis);
                Check(PaperRiskViolationCode.MinimumCashReserve, b.AvailableCash, -debit, reserve, b.Exchange, b.Currency, minimum: true);
                Check(PaperRiskViolationCode.SingleExecutionDebitLimit, 0, debit, checked(b.InitialCash * l.MaximumSingleExecutionDebitFraction), b.Exchange, b.Currency);
                Check(PaperRiskViolationCode.TotalOpenCostBasisLimit, cost, delta, checked(b.InitialCash * l.MaximumOpenCostBasisFraction), b.Exchange, b.Currency);
                var marketLimit = checked(b.InitialCash * l.MaximumMarketCostBasisFraction);
                var instrumentLimit = checked(b.InitialCash * l.MaximumInstrumentCostBasisFraction);
                foreach (var market in current.Concat(next).Select(p => p.MarketId).Distinct())
                {
                    var c = current.Where(p => p.MarketId == market).Sum(p => p.CostBasis); var d = next.Where(p => p.MarketId == market).Sum(p => p.CostBasis);
                    Check(PaperRiskViolationCode.MarketCostBasisLimit, c, d, marketLimit, b.Exchange, b.Currency, market);
                    exposures.Add(new(b.Exchange, b.Currency, market, null, c, d, checked(c + d), marketLimit, checked(marketLimit - c - d)));
                }
                foreach (var instrument in current.Concat(next).Select(p => (p.MarketId, p.InstrumentId, p.Outcome)).Distinct())
                {
                    bool Matches(PaperRiskPosition p) => (p.MarketId, p.InstrumentId, p.Outcome) == instrument;
                    var c = current.Where(Matches).Sum(p => p.CostBasis); var d = next.Where(Matches).Sum(p => p.CostBasis);
                    Check(PaperRiskViolationCode.InstrumentCostBasisLimit, c, d, instrumentLimit, b.Exchange, b.Currency, instrument.MarketId, instrument.InstrumentId);
                    exposures.Add(new(b.Exchange, b.Currency, instrument.MarketId, instrument.InstrumentId, c, d, checked(c + d), instrumentLimit, checked(instrumentLimit - c - d)));
                }
                buckets.Add(new(b.Exchange, b.Currency, b.InitialCash, b.AvailableCash, debit, checked(b.AvailableCash - debit), reserve,
                    checked(b.AvailableCash - debit - reserve), cost, checked(cost + delta), checked(b.InitialCash * l.MaximumOpenCostBasisFraction),
                    checked(b.InitialCash * l.MaximumOpenCostBasisFraction - cost - delta),
                    current.GroupBy(p => p.MarketId).Select(g => g.Sum(p => p.CostBasis)).DefaultIfEmpty().Max(), marketLimit,
                    current.Select(p => p.CostBasis).DefaultIfEmpty().Max(), instrumentLimit));
            }
            if (plan is not null)
            {
                Check(PaperRiskViolationCode.RequestedQuantityLimit, 0, plan.Quantity, l.MaximumRequestedQuantity);
                if (plan.Proof.Fees?.FeeAdjustedEdgePerShare is not { } edge || plan.Proof.Fees.FeeAdjustedGuaranteedProfit is not { } profit)
                { violations.Add(new(PaperRiskViolationCode.IntegrityFailure)); return Result(PaperRiskOutcome.InvalidFinancialState); }
                Check(PaperRiskViolationCode.MinimumEntryEdge, 0, edge, l.MinimumFeeAdjustedEdgePerShare, minimum: true);
                Check(PaperRiskViolationCode.MinimumEntryProfit, 0, profit, l.MinimumFeeAdjustedProfit, minimum: true);
            }
            return Result(violations.Count == 0 ? PaperRiskOutcome.Approved : PaperRiskOutcome.Rejected);
        }
        catch (OverflowException) { violations.Add(new(PaperRiskViolationCode.ArithmeticOverflow)); return Result(PaperRiskOutcome.InvalidFinancialState); }
    }
}
