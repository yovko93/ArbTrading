using System.Collections.Immutable;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Execution;

public enum PaperRejection
{
    None, NotPaperMode, OpportunityNotFound, OpportunityStale, RelationshipIneligible, ManualRelationshipNotAllowed,
    BookUnavailable, BookStale, BookContinuityInsufficient, MarketDataChanged, BookSkewTooLarge, LiquidityConflict,
    InsufficientDepth, RequestedQuantityInvalid, InsufficientPaperFunds, FeeModelUnresolved, FeeAdjustedNoEdge,
    FeeChanged, CurrencyModelUnsupported, ArithmeticOverflow, DuplicateRequest, AccountUninitialized,
    GenerationChanged, PreviewExpired, IntegrityFailure, ConfirmationRequired
}
public enum PaperExecutionState { PendingValidation, Rejected, Committed, Settled }
public enum PaperIntegrity { Healthy, NeedsReconciliation, Corrupt }
public sealed record PaperFill(Guid Id, Guid LegId, OrderBookInstrumentId Instrument, decimal Quantity, decimal Price,
    decimal Notional, decimal Fee, string Currency, LiquidityOrigin Origin, LiquiditySourceId NativeLiquidityIdentity,
    long BookVersion, DateTimeOffset FilledAt, FeeQuote FeeProof);
public sealed record PaperDebit(string Exchange, string Currency, decimal Notional, decimal Fees)
{
    public decimal Total => checked(Notional + Fees);
}
public sealed record PaperPlan(Guid Id, decimal Quantity, ArbitrageOpportunitySnapshot Proof, ImmutableArray<PaperFill> Fills,
    ImmutableArray<PaperDebit> Debits)
{
    public decimal Cost => Debits.Sum(d => d.Total);
    public decimal ExpectedPayoutAtResolution => Proof.GuaranteedGrossPayout;
    public decimal ExpectedProfitAtResolution => checked(ExpectedPayoutAtResolution - Cost);
}
public sealed record PaperPlanResult(PaperRejection Rejection, PaperPlan? Plan = null);

// A projection of the existing paired depth walk, never a second independent liquidity walk.
public static class PaperPlanner
{
    public const decimal MaximumQuantity = 1000m;
    public const decimal MinimumFeeAdjustedEdge = .001m;
    public static readonly TimeSpan PreviewMaximumAge = TimeSpan.FromSeconds(5);
    public static PaperRejection Eligibility(ArbitrageOpportunitySnapshot s, decimal quantity)
    {
        if (quantity is <= 0 or > MaximumQuantity) return PaperRejection.RequestedQuantityInvalid;
        if (s.RelationshipTrust != RelationshipTrust.Deterministic) return PaperRejection.ManualRelationshipNotAllowed;
        if (!s.RelationshipEligible) return PaperRejection.RelationshipIneligible;
        if (s.Status != OpportunityStatus.Detected) return s.Status switch
        {
            OpportunityStatus.BookStale => PaperRejection.BookStale,
            OpportunityStatus.BookContinuityInsufficient => PaperRejection.BookContinuityInsufficient,
            OpportunityStatus.BookSkewTooLarge => PaperRejection.BookSkewTooLarge,
            OpportunityStatus.LiquidityConflict => PaperRejection.LiquidityConflict,
            OpportunityStatus.InsufficientLiquidity => PaperRejection.InsufficientDepth,
            OpportunityStatus.RelationshipIneligible => PaperRejection.RelationshipIneligible,
            OpportunityStatus.BooksChangedDuringEvaluation or OpportunityStatus.StaleInput => PaperRejection.MarketDataChanged,
            OpportunityStatus.ArithmeticOverflow => PaperRejection.ArithmeticOverflow,
            OpportunityStatus.NoGrossEdge => PaperRejection.FeeAdjustedNoEdge,
            _ => PaperRejection.BookUnavailable
        };
        if (!s.BooksActionable) return PaperRejection.BookUnavailable;
        if (!s.SkewAcceptable) return PaperRejection.BookSkewTooLarge;
        if (s.Legs.Length != 2 || s.Legs.Any(l => l.Action != DepthAction.Buy)) return PaperRejection.RelationshipIneligible;
        if (!s.FullyExecutableForRequestedQuantity || s.PairedQuantity != quantity || s.Segments.IsDefaultOrEmpty)
            return PaperRejection.InsufficientDepth;
        var f = s.Fees;
        if (f is null || f.Breakdown.IsDefaultOrEmpty) return PaperRejection.FeeModelUnresolved;
        if (f.Breakdown.Any(q => q.Currency is not ("USD" or "USDC")) || f.Breakdown.Select(q => q.Currency).Distinct().Count() != 1)
            return PaperRejection.CurrencyModelUnsupported;
        if (f.State == FeeOpportunityStatus.FeeResultStale) return PaperRejection.FeeChanged;
        if (f.Breakdown.Any(q => q.TotalFee is null or < 0) || f.TotalExchangeFees is null)
            return PaperRejection.FeeModelUnresolved;
        if (f.State != FeeOpportunityStatus.FeeAdjustedDetected || f.FeeAdjustedGuaranteedProfit is not > 0 ||
            f.FeeAdjustedEdgePerShare is not >= MinimumFeeAdjustedEdge) return PaperRejection.FeeAdjustedNoEdge;
        return PaperRejection.None;
    }
    public static PaperPlanResult Create(ArbitrageOpportunitySnapshot s, IReadOnlyList<CachedOrderBook> books, decimal quantity, DateTimeOffset at)
    {
        var error = Eligibility(s, quantity);
        if (error != PaperRejection.None) return new(error);
        try
        {
            if (books.Count != s.Legs.Length) return new(PaperRejection.BookUnavailable);
            var fills = ImmutableArray.CreateBuilder<PaperFill>();
            var sources = new HashSet<LiquiditySourceId>();
            for (var i = 0; i < s.Legs.Length; i++)
            {
                var leg = s.Legs[i]; var book = books[i]; var legId = Guid.NewGuid();
                if (book.Version != leg.SnapshotVersion) return new(PaperRejection.MarketDataChanged);
                if (book.Snapshot is null || !book.Eligibility.IsActionable || book.Snapshot.Instrument != leg.Instrument)
                    return new(PaperRejection.BookUnavailable);
                foreach (var group in s.Segments.GroupBy(p => i == 0 ? p.LegAPrice : p.LegBPrice))
                {
                    var qty = group.Sum(p => p.Quantity);
                    var level = book.Snapshot.Asks.SingleOrDefault(l => l.Price == group.Key);
                    if (level is null || qty <= 0 || qty > level.Quantity) return new(PaperRejection.InsufficientDepth);
                    var native = book.Snapshot.LiquiditySource(level);
                    if (native is null || !sources.Add(native)) return new(PaperRejection.LiquidityConflict);
                    var quotes = s.Fees!.Breakdown.Where(q => q.Context.Exchange == leg.Instrument.Exchange &&
                        q.Context.MarketId == leg.Instrument.NativeMarketId && q.Context.InstrumentId == leg.Instrument.NativeInstrumentId &&
                        q.Context.Price == group.Key && q.Context.Quantity == qty).ToArray();
                    if (quotes.Length != 1 || quotes[0].TotalFee is not { } fee) return new(PaperRejection.FeeModelUnresolved);
                    fills.Add(new(Guid.NewGuid(), legId, leg.Instrument, qty, group.Key, checked(qty * group.Key), fee,
                        quotes[0].Currency, level.Origin, native, leg.SnapshotVersion, at, quotes[0]));
                }
            }
            if (fills.Sum(f => f.Notional) != s.GrossCost || fills.Sum(f => f.Fee) != s.Fees!.TotalExchangeFees)
                return new(PaperRejection.FeeModelUnresolved);
            var debits = fills.GroupBy(f => (f.Instrument.Exchange, f.Currency))
                .Select(g => new PaperDebit(g.Key.Exchange, g.Key.Currency, g.Sum(f => f.Notional), g.Sum(f => f.Fee))).ToImmutableArray();
            var plan = new PaperPlan(Guid.NewGuid(), quantity, s, fills.ToImmutable(), debits);
            _ = plan.Cost;
            return new(PaperRejection.None, plan);
        }
        catch (OverflowException) { return new(PaperRejection.ArithmeticOverflow); }
    }
}

public static class PaperAccounting
{
    public static decimal Debit(decimal available, decimal amount)
    {
        if (amount < 0 || available < amount) throw new InvalidOperationException("Insufficient paper funds.");
        return checked(available - amount);
    }
    // Cost basis includes modeled fees. Average entry excludes fees; no realization or settlement exists.
    public static (decimal Quantity, decimal CostBasis, decimal Fees, decimal AverageEntry) Accumulate(
        decimal quantity, decimal costBasis, decimal fees, PaperFill fill)
    {
        checked
        {
            if (quantity < 0 || costBasis < 0 || fees < 0 || fill.Quantity <= 0 || fill.Notional < 0 || fill.Fee < 0)
                throw new ArgumentOutOfRangeException(nameof(fill));
            var q = quantity + fill.Quantity; var f = fees + fill.Fee; var c = costBasis + fill.Notional + fill.Fee;
            return (q, c, f, (c - f) / q);
        }
    }
}
