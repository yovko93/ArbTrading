using System.Text.Json;
using Arbitrage.Execution;

namespace Arbitrage.Application.Tests;

public sealed class PaperSizingTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    internal static PaperAutomationSettings Settings(decimal min = 5, decimal max = 25, decimal step = 5) =>
        new(PaperSizingMode.LargestAdmissibleGridQuantity, 1, .005m, .05m, 10, 10, 2, 5, 60, .25m, 20, true, true, min, max, step);
    private static PaperSizingCandidate Candidate(decimal q, bool approved)
    {
        var (s, books) = PaperTests.Fixture(q); var plan = PaperPlanner.Create(s, books, q, At).Plan!;
        var limits = new PaperRiskLimits(0, 1, 1, 1, 1, 100, 100, 100, 1000, .001m, 0);
        var profile = new PaperRiskProfile(1, Guid.NewGuid(), limits, At, At, Guid.NewGuid(), limits.Fingerprint);
        var risk = PaperRiskEvaluator.Evaluate(profile, new(Guid.NewGuid(), 0, true, [new("Kalshi", "USD", 100, 100, 1), new("Polymarket", "USD", 100, 100, 1)], [], []), plan, At);
        return new(plan, risk, approved ? null : "FixtureRejected");
    }
    [Fact] public void Grid_uses_exact_min_plus_n_step_without_inventing_endpoint()
    {
        Assert.True(PaperQuantityGrid.TryCreate(Settings(2.5m, 10, 2.5m), out var grid)); Assert.Equal(new[] { 10m, 7.5m, 5m, 2.5m }, grid);
        Assert.True(PaperQuantityGrid.TryCreate(Settings(.1m, 1, .2m), out grid)); Assert.Equal(new[] { .9m, .7m, .5m, .3m, .1m }, grid);
        Assert.True(PaperQuantityGrid.TryCreate(Settings(1, 1000, decimal.MaxValue), out grid)); Assert.Equal(new[] { 1m }, grid);
        Assert.False(PaperQuantityGrid.TryCreate(Settings(1, 1000, .0000000000000000000000000001m), out _));
    }
    [Theory] [InlineData(1, 256, 1, true)] [InlineData(1, 257, 1, false)] [InlineData(0, 25, 1, false)]
    [InlineData(5, 4, 1, false)] [InlineData(1, 25, 0, false)] [InlineData(1, 1001, 1, false)]
    public void Grid_bounds_are_rejected_without_truncation(decimal min, decimal max, decimal step, bool valid) =>
        Assert.Equal(valid, PaperQuantityGrid.TryCreate(Settings(min, max, step), out _));
    [Fact] public async Task Nonmonotonic_admission_selects_first_descending_success_and_stops()
    {
        var visited = new List<decimal>();
        var decision = await PaperSizer.SearchAsync(Settings(max: 20), (q, _) => { visited.Add(q); return Task.FromResult(Candidate(q, q is 15 or 5)); }, new(), At, default);
        Assert.Equal(PaperSizingState.Selected, decision.State); Assert.Equal(15, decision.SelectedQuantity);
        Assert.Equal(new[] { 20m, 15m }, visited); Assert.Equal(2, decision.CandidatesEvaluated);
    }
    [Fact] public async Task No_admissible_never_goes_below_minimum_and_budget_resets_next_cycle()
    {
        var settings = Settings(1, 256, 1); var budget = new PaperSizingBudget(); var evaluations = 0;
        Task<PaperSizingCandidate> Reject(decimal q, CancellationToken ct) { evaluations++; return Task.FromResult(new PaperSizingCandidate(null, null, "Risk:MarketCostBasisLimit")); }
        for (var i = 0; i < 2; i++) { var d = await PaperSizer.SearchAsync(settings, Reject, budget, At, default); Assert.Equal(PaperSizingState.NoAdmissibleQuantity, d.State); Assert.Equal(256, d.CandidatesEvaluated); }
        var deferred = await PaperSizer.SearchAsync(settings, Reject, budget, At, default);
        Assert.Equal(PaperSizingState.EvaluationBudgetExceeded, deferred.State); Assert.Equal(512, evaluations); Assert.Equal(0, deferred.CandidatesEvaluated);
        Assert.Equal(256, (await PaperSizer.SearchAsync(settings, Reject, new(), At, default)).CandidatesEvaluated);
        var seen = new List<decimal>(); await PaperSizer.SearchAsync(Settings(), (q, _) => { seen.Add(q); return Task.FromResult(new PaperSizingCandidate(null, null, "InsufficientDepth")); }, new(), At, default);
        Assert.Equal(new[] { 25m, 20m, 15m, 10m, 5m }, seen);
    }
    [Fact] public async Task Independent_failure_overflow_and_cancellation_stop_boundedly()
    {
        var d = await PaperSizer.SearchAsync(Settings(), (_, _) => Task.FromResult(new PaperSizingCandidate(null, null, "FeeModelUnresolved", true)), new(), At, default);
        Assert.Equal(PaperSizingState.InputUnavailable, d.State); Assert.Equal(1, d.CandidatesEvaluated);
        d = await PaperSizer.SearchAsync(Settings(), (_, _) => throw new OverflowException(), new(), At, default);
        Assert.Equal(PaperSizingState.ArithmeticOverflow, d.State);
        await Assert.ThrowsAsync<OperationCanceledException>(() => PaperSizer.SearchAsync(Settings(), (_, _) => throw new Exception(), new(), At, new(true)));
    }
    [Fact] public void Legacy_settings_canonical_hash_remains_identical_to_frozen_v1_shape()
    {
        // Exact Phase 04E shape, deliberately independent of the extended record serializer.
        var legacy = new { SizingMode = PaperSizingMode.FixedQuantity, FixedQuantity = 10m, MinimumFeeAdjustedEdgePerShare = .005m,
            MinimumFeeAdjustedProfit = .05m, MaximumExecutionsPerSession = 10, MaximumExecutionsPerHour = 10,
            MaximumExecutionsPerRelationshipPerSession = 2, MinimumSecondsBetweenExecutions = 5, RelationshipCooldownSeconds = 60,
            MaximumSessionDebitFractionPerBucket = .25m, MaximumCandidatesPerCycle = 20, RequireRealtime = true, AllowPolymarketBestEffort = true };
        var settings = JsonSerializer.Deserialize<PaperAutomationSettings>(JsonSerializer.Serialize(legacy))!;
        Assert.Equal(PaperAutomationPolicy.Hash(legacy), PaperAutomationPolicy.Hash(settings));
        Assert.DoesNotContain("MinimumQuantity", JsonSerializer.Serialize(settings));
        var session = Guid.Parse("12345678-1234-1234-1234-123456789012"); var key = new string('A', 64); var stamp = new string('B', 64);
        var legacyId = new Guid(Convert.FromHexString(PaperAutomationPolicy.Hash(new { session, key, stamp, quantity = 10m }))[..16]);
        Assert.Equal(legacyId, PaperAutomationPolicy.RequestId(session, key, stamp, 10, null));
    }
}
