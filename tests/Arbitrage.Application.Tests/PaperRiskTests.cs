using System.Collections.Immutable;
using Arbitrage.Execution;

namespace Arbitrage.Application.Tests;

public sealed class PaperRiskTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    private static readonly PaperRiskLimits Wide = new(0, 1, 1, 1, 1, 1000, 1000, 1000, 1000, .001m, 0);
    private static PaperRiskProfile Profile(PaperRiskLimits l) => new(1, Guid.NewGuid(), l, At, At, Guid.NewGuid(), l.Fingerprint);
    private static PaperPlan Plan(decimal debit)
    {
        var (s, b) = PaperTests.Fixture(); var p = PaperPlanner.Create(s, b, 10, At).Plan!;
        return p with { Fills = [p.Fills[0] with { Notional = debit, Fee = 0 }], Debits = [new("Kalshi", "USD", debit, 0)] };
    }
    private static CurrentPaperRiskState State(decimal cost = 0) => new(Guid.NewGuid(), 0, true, [new("Kalshi", "USD", 100, 100, 1)],
        cost == 0 ? [] : [new("Kalshi", "USD", "a", "yes", "Yes", cost)], []);
    [Theory]
    [InlineData("reserve", 80, 0, false)] [InlineData("reserve", 81, 0, true)]
    [InlineData("single", 10, 0, false)] [InlineData("single", 10.01, 0, true)]
    [InlineData("open", 15, 45, false)] [InlineData("open", 15.01, 45, true)]
    [InlineData("market", 8, 12, false)] [InlineData("market", 8.01, 12, true)]
    [InlineData("instrument", 5, 10, false)] [InlineData("instrument", 5.01, 10, true)]
    public void Exact_decimal_limits_allow_equality(string dimension, decimal debit, decimal cost, bool rejected)
    {
        var (limits, code) = dimension switch {
            "reserve" => (Wide with { MinimumCashReserveFraction = .2m }, PaperRiskViolationCode.MinimumCashReserve),
            "single" => (Wide with { MaximumSingleExecutionDebitFraction = .1m }, PaperRiskViolationCode.SingleExecutionDebitLimit),
            "open" => (Wide with { MaximumOpenCostBasisFraction = .6m, MaximumMarketCostBasisFraction = .6m, MaximumInstrumentCostBasisFraction = .6m }, PaperRiskViolationCode.TotalOpenCostBasisLimit),
            "market" => (Wide with { MaximumMarketCostBasisFraction = .2m, MaximumInstrumentCostBasisFraction = .2m }, PaperRiskViolationCode.MarketCostBasisLimit),
            _ => (Wide with { MaximumInstrumentCostBasisFraction = .15m }, PaperRiskViolationCode.InstrumentCostBasisLimit) };
        var result = PaperRiskEvaluator.Evaluate(Profile(limits), State(cost), Plan(debit), At);
        Assert.Equal(rejected, result.Violations.Any(v => v.Code == code));
        Assert.Equal(rejected ? PaperRiskOutcome.Rejected : PaperRiskOutcome.Approved, result.Decision);
        if (rejected) Assert.All(result.Violations.Where(v => v.Code == code), v => Assert.True(v.RemainingHeadroom < 0));
    }
    [Theory] [InlineData(1, false, 1, false)] [InlineData(1, true, 2, false)] [InlineData(2, true, 3, true)]
    public void Position_count_uses_distinct_materialized_native_identity(int existing, bool newInstrument, int projected, bool rejected)
    {
        var state = State(1); if (existing == 2) state = state with { Positions = state.Positions.Add(new("Kalshi", "USD", "b", "no", "No", 1)) };
        var p = Plan(1); if (newInstrument) p = p with { Fills = [p.Fills[0] with { Instrument = new("Kalshi", "c", "new", "Yes") }] };
        var result = PaperRiskEvaluator.Evaluate(Profile(Wide with { MaximumOpenPositions = 2 }), state, p, At);
        Assert.Equal(projected, result.ProjectedOpenPositions); Assert.Equal(rejected, result.Violations.Any(v => v.Code == PaperRiskViolationCode.OpenPositionCountLimit));
    }
    [Theory] [InlineData(2, false)] [InlineData(3, true)]
    public void Open_execution_count_equality(int count, bool rejected)
    {
        var state = State() with { Executions = Enumerable.Range(0, count).Select(_ => new PaperRiskOpenExecution(Guid.NewGuid())).ToImmutableArray() };
        var r = PaperRiskEvaluator.Evaluate(Profile(Wide with { MaximumOpenExecutions = 3, MaximumOpenExecutionsPerRelationship = 2 }), state, Plan(1), At);
        Assert.Equal(rejected, r.Violations.Any(v => v.Code == PaperRiskViolationCode.OpenExecutionCountLimit));
    }
    [Fact] public void Relationship_repetition_is_by_id()
    {
        var p = Plan(1); var state = State() with { Executions = [new(p.Proof.RelationshipId), new(p.Proof.RelationshipId)] };
        var profile = Profile(Wide with { MaximumOpenExecutionsPerRelationship = 2 });
        Assert.Contains(PaperRiskEvaluator.Evaluate(profile, state, p, At).Violations, v => v.Code == PaperRiskViolationCode.RelationshipExecutionCountLimit);
        Assert.Equal(PaperRiskOutcome.Approved, PaperRiskEvaluator.Evaluate(profile, state, p with { Proof = p.Proof with { RelationshipId = Guid.NewGuid() } }, At).Decision);
    }
    [Theory] [InlineData(.0029, .99, true)] [InlineData(.003, 1, false)]
    public void Uses_exact_requested_quantity_fee_economics(decimal edge, decimal profit, bool rejected)
    {
        var p = Plan(1); p = p with { Proof = p.Proof with { Fees = p.Proof.Fees! with { FeeAdjustedEdgePerShare = edge, FeeAdjustedGuaranteedProfit = profit } } };
        var r = PaperRiskEvaluator.Evaluate(Profile(Wide with { MinimumFeeAdjustedEdgePerShare = .003m, MinimumFeeAdjustedProfit = 1 }), State(), p, At);
        Assert.Equal(rejected, r.Violations.Any(v => v.Code == PaperRiskViolationCode.MinimumEntryEdge));
        Assert.Equal(rejected, r.Violations.Any(v => v.Code == PaperRiskViolationCode.MinimumEntryProfit));
    }
    [Fact] public void Buckets_never_net_USD_and_USDC()
    {
        var p = Plan(10); var second = p.Fills[0] with { Instrument = new("Polymarket", "b", "456", "No"), Currency = "USDC", Notional = 5 };
        p = p with { Fills = p.Fills.Add(second), Debits = p.Debits.Add(new("Polymarket", "USDC", 5, 0)) };
        var s = State() with { Buckets = [new("Kalshi", "USD", 100, 100, 1), new("Polymarket", "USDC", 20, 8, 1)] };
        var r = PaperRiskEvaluator.Evaluate(Profile(Wide with { MinimumCashReserveFraction = .2m }), s, p, At);
        Assert.Equal(PaperRiskOutcome.Rejected, r.Decision); var v = Assert.Single(r.Violations); Assert.Equal("USDC", v.Currency); Assert.Equal(3, v.ProjectedValue); Assert.Equal(4, v.LimitValue);
    }
    [Fact] public void Missing_invalid_overflow_and_quantity_fail_closed()
    {
        Assert.Equal(PaperRiskOutcome.NotConfigured, PaperRiskEvaluator.Evaluate(null, State(), Plan(1), At).Decision);
        Assert.Equal(PaperRiskOutcome.InvalidFinancialState, PaperRiskEvaluator.Evaluate(Profile(Wide with { MaximumRequestedQuantity = 1001 }), State(), Plan(1), At).Decision);
        Assert.Contains(PaperRiskEvaluator.Evaluate(Profile(Wide with { MaximumRequestedQuantity = 1 }), State(), Plan(1), At).Violations, v => v.Code == PaperRiskViolationCode.RequestedQuantityLimit);
        var s = State(decimal.MaxValue);
        Assert.Contains(PaperRiskEvaluator.Evaluate(Profile(Wide), s, Plan(1), At).Violations, v => v.Code == PaperRiskViolationCode.ArithmeticOverflow);
        Assert.Equal(Wide.Fingerprint, (Wide with { MaximumOpenCostBasisFraction = 1.00m }).Fingerprint);
    }
}
