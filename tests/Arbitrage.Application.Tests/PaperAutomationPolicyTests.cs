using System.Collections.Immutable;
using System.Globalization;
using Arbitrage.Application;
using Arbitrage.Domain;
using Arbitrage.Execution;
namespace Arbitrage.Application.Tests;

public sealed class PaperAutomationPolicyTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    private static PaperAutomationSettings Settings => new(PaperSizingMode.FixedQuantity, 10, .005m, .05m, 10, 10, 2, 5, 60, .25m, 20, true, true);
    private static PaperAutomationProfile Profile(PaperAutomationSettings? settings = null) => new(1, Guid.NewGuid(), settings ?? Settings, At, At, Guid.NewGuid(), PaperAutomationPolicy.Hash(settings ?? Settings));
    private static PaperPlan Plan()
    {
        var (s, books) = PaperTests.Fixture(); var p = PaperPlanner.Create(s, books, 10, At).Plan!;
        return p with { Proof = p.Proof with { Legs = [.. p.Proof.Legs.Select(l => l with { SourceMode = BookSourceMode.Realtime,
            Continuity = l.Instrument.Exchange == "Kalshi" ? BookContinuity.Continuous : BookContinuity.BestEffort })] } };
    }
    private static readonly PaperAutomationHistory Empty = new(0, 0, 0, null, null, false, []);
    private static readonly PaperRiskBucket[] Buckets = [new("Kalshi", "USD", 100, 100, 1), new("Polymarket", "USD", 100, 100, 1)];
    [Fact] public void Valid_profile_tightens_risk_and_fails_closed_for_unsupported_sizing()
    {
        var limits = new PaperRiskLimits(.2m, .2m, .6m, .4m, .4m, 20, 10, 2, 10, .005m, .05m);
        var risk = new PaperRiskProfile(1, Guid.NewGuid(), limits, At, At, Guid.NewGuid(), limits.Fingerprint);
        Assert.True(Settings.Valid(risk)); Assert.True(Profile().Valid(risk)); Assert.False(Settings.Valid(null));
        Assert.False((Settings with { FixedQuantity = 11 }).Valid(risk)); Assert.False((Settings with { FixedQuantity = 0 }).Valid(risk));
        Assert.False((Settings with { MinimumFeeAdjustedEdgePerShare = .001m }).Valid(risk));
        Assert.False((Settings with { MinimumFeeAdjustedProfit = 0 }).Valid(risk));
        Assert.False((Settings with { RequireRealtime = false }).Valid(risk)); Assert.False((Settings with { SizingMode = (PaperSizingMode)99 }).Valid(risk));
        Assert.False((Profile() with { PolicyVersion = 99 }).Valid(risk));
    }
    [Theory] [InlineData("quantity", PaperAutomationReason.InsufficientDepth)] [InlineData("edge", PaperAutomationReason.MinimumEdgeRejected)]
    [InlineData("profit", PaperAutomationReason.MinimumProfitRejected)] [InlineData("session", PaperAutomationReason.SessionExecutionLimitReached)]
    [InlineData("hour", PaperAutomationReason.HourlyExecutionLimitReached)] [InlineData("relationship", PaperAutomationReason.RelationshipSessionLimit)]
    [InlineData("opportunityCooldown", PaperAutomationReason.CooldownRejected)] [InlineData("relationshipCooldown", PaperAutomationReason.CooldownRejected)]
    [InlineData("duplicate", PaperAutomationReason.DuplicateSuppressed)] [InlineData("budget", PaperAutomationReason.AutomationSessionDebitLimit)]
    public void Exact_policy_boundaries(string variant, PaperAutomationReason expected)
    {
        var p = Plan(); var settings = Settings; var history = Empty;
        if (variant == "quantity") settings = settings with { FixedQuantity = 9 };
        if (variant == "edge") settings = settings with { MinimumFeeAdjustedEdgePerShare = .9m };
        if (variant == "profit") settings = settings with { MinimumFeeAdjustedProfit = 10 };
        history = variant switch { "session" => history with { SessionExecutions = 10 }, "hour" => history with { HourlyExecutions = 10 },
            "relationship" => history with { RelationshipSessionExecutions = 2 }, "opportunityCooldown" => history with { LastOpportunityAt = At.AddSeconds(-4) },
            "relationshipCooldown" => history with { LastRelationshipAt = At.AddSeconds(-59) }, "duplicate" => history with { DuplicateInput = true },
            "budget" => history with { SessionDebits = [new("Kalshi", "USD", 24, 0)] }, _ => history };
        Assert.Equal(expected, PaperAutomationPolicy.Evaluate(settings, p, history, Buckets, At));
        Assert.Equal(PaperAutomationReason.None, PaperAutomationPolicy.Evaluate(Settings, p, Empty with { LastOpportunityAt = At.AddSeconds(-5), LastRelationshipAt = At.AddSeconds(-60) }, Buckets, At));
    }
    [Theory] [InlineData(BookContinuity.AwaitingAnchor)] [InlineData(BookContinuity.GapDetected)] [InlineData(BookContinuity.Resynchronizing)]
    [InlineData(BookContinuity.Disconnected)] [InlineData(BookContinuity.BestEffort)]
    public void Kalshi_requires_continuous_and_poly_requires_acknowledged_best_effort(BookContinuity continuity)
    {
        var p = Plan(); Assert.Equal(PaperAutomationReason.None, PaperAutomationPolicy.Quality(p.Proof, Settings));
        Assert.Equal(PaperAutomationReason.InputQualityRejected, PaperAutomationPolicy.Quality(p.Proof with { Legs = [p.Proof.Legs[0] with { Continuity = continuity }, p.Proof.Legs[1]] }, Settings));
        Assert.Equal(PaperAutomationReason.InputQualityRejected, PaperAutomationPolicy.Quality(p.Proof, Settings with { AllowPolymarketBestEffort = false }));
        Assert.Equal(PaperAutomationReason.InputQualityRejected, PaperAutomationPolicy.Quality(p.Proof with { Legs = [p.Proof.Legs[0] with { SourceMode = BookSourceMode.RestSnapshot }, p.Proof.Legs[1]] }, Settings));
    }
    [Fact] public void Stamp_is_culture_scale_and_time_independent_but_changes_with_input_versions()
    {
        var p = Profile(); var s = Plan().Proof; var stamp = PaperAutomationPolicy.Stamp(s, p); var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(stamp, PaperAutomationPolicy.Stamp(s with { EvaluatedAt = At.AddMinutes(1) }, p with { Settings = Settings with { FixedQuantity = 10.00m } })); }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.NotEqual(stamp, PaperAutomationPolicy.Stamp(s with { Legs = [s.Legs[0] with { SnapshotVersion = 999 }, s.Legs[1]] }, p));
        Assert.NotEqual(stamp, PaperAutomationPolicy.Stamp(s, p with { Revision = Guid.NewGuid() }));
        var session = Guid.NewGuid(); Assert.Equal(PaperAutomationPolicy.RequestId(session, s.OpportunityKey, stamp, 10), PaperAutomationPolicy.RequestId(session, s.OpportunityKey, stamp, 10.00m));
        Assert.NotEqual(PaperAutomationPolicy.RequestId(session, s.OpportunityKey, stamp, 10), PaperAutomationPolicy.RequestId(Guid.NewGuid(), s.OpportunityKey, stamp, 10));
    }
    [Fact] public void Session_budget_is_gross_by_bucket_and_never_spends_other_currency()
    {
        var p = Plan(); var buckets = Buckets.Append(new PaperRiskBucket("Kalshi", "USDC", 100000, 100000, 1)).ToArray();
        Assert.Equal(PaperAutomationReason.AutomationSessionDebitLimit, PaperAutomationPolicy.Evaluate(Settings, p, Empty with { SessionDebits = [new("Kalshi", "USD", 24, 0)] }, buckets, At));
        Assert.Equal(PaperAutomationReason.None, PaperAutomationPolicy.Evaluate(Settings, p, Empty with { SessionDebits = [new("Kalshi", "USDC", 1000, 0)] }, buckets, At));
        Assert.Equal(PaperAutomationReason.ArithmeticOverflow, PaperAutomationPolicy.Evaluate(Settings, p, Empty with { SessionDebits = [new("Kalshi", "USD", decimal.MaxValue, 0)] }, buckets, At));
    }
}
