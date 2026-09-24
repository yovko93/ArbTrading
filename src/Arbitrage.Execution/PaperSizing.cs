using System.Collections.Immutable;

namespace Arbitrage.Execution;

// A paper simulation grid, not an exchange lot-size rule. No floating point or monotonicity assumptions.
public static class PaperQuantityGrid
{
    public const int MaximumAdaptiveSizingCandidates = 256;
    public static bool TryCreate(PaperAutomationSettings settings, out ImmutableArray<decimal> descending)
    {
        descending = [];
        if (settings.MinimumQuantity is not { } min || settings.MaximumQuantity is not { } max || settings.QuantityStep is not { } step ||
            min <= 0 || max < min || max > PaperPlanner.MaximumQuantity || step <= 0) return false;
        try
        {
            var points = ImmutableArray.CreateBuilder<decimal>();
            // Bounded construction avoids division rounding near integer boundaries and overflow for tiny steps.
            for (var n = 0; n <= MaximumAdaptiveSizingCandidates; n++)
            {
                decimal q;
                try { q = checked(min + checked(n * step)); }
                catch (OverflowException) { break; } // Positive arithmetic exceeding decimal.MaxValue is necessarily above max <= 1000.
                if (q > max) break;
                if (n == MaximumAdaptiveSizingCandidates) return false;
                if (points.Count != 0 && q <= points[^1]) return false;
                points.Add(q);
            }
            points.Reverse(); descending = points.ToImmutable(); return !descending.IsEmpty;
        }
        catch (OverflowException) { return false; }
    }
    public static bool Contains(PaperAutomationSettings settings, decimal quantity) => TryCreate(settings, out var grid) && grid.Contains(quantity);
}

public enum PaperSizingState { Selected, NoAdmissibleQuantity, InputUnavailable, EvaluationBudgetExceeded, ArithmeticOverflow }
public sealed record PaperSizingRejection(string Reason, int Count);
public sealed record PaperSizingCandidate(PaperPlan? Plan, PaperRiskDecision? Risk, string? Rejection, bool InputUnavailable = false);
public sealed record PaperSizingDecision(PaperSizingState State, PaperSizingMode Mode, decimal? SelectedQuantity, int CandidatesEvaluated,
    decimal HighestConfiguredQuantity, decimal LowestConfiguredQuantity, decimal QuantityStep, ImmutableArray<PaperSizingRejection> Rejections,
    PaperPlan? SelectedPlan, PaperRiskDecision? SelectedRiskDecision, DateTimeOffset EvaluatedAt, PaperSizingProof? Proof = null);
public sealed record PaperSizingProof(int Version, PaperSizingMode Mode, decimal SelectedQuantity, decimal MinimumQuantity, decimal MaximumQuantity,
    decimal QuantityStep, int CandidatesEvaluated, ImmutableArray<PaperSizingRejection> Rejections, string TriggerInputStamp,
    Guid AutomationProfileRevision, Guid RiskPolicyRevision, Guid GenerationId, long FinancialRevision, string PlanFingerprint,
    ImmutableArray<PaperDebit> SessionDebits, string DecisionFingerprint);

// Reserve enough room to complete one search. An unstarted opportunity remains eligible next cycle.
public sealed class PaperSizingBudget(int maximum = PaperSizingBudget.MaximumAdaptiveEvaluationsPerCycle)
{
    public const int MaximumAdaptiveEvaluationsPerCycle = 512;
    public int Remaining { get; private set; } = maximum is >= 0 and <= MaximumAdaptiveEvaluationsPerCycle ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
    public bool CanComplete(int count) => count > 0 && count <= Remaining;
    public void Consume() { if (Remaining <= 0) throw new InvalidOperationException("Sizing budget exhausted."); Remaining--; }
}

public static class PaperSizer
{
    public static async Task<PaperSizingDecision> SearchAsync(PaperAutomationSettings settings, Func<decimal, CancellationToken, Task<PaperSizingCandidate>> evaluate,
        PaperSizingBudget budget, DateTimeOffset at, CancellationToken ct)
    {
        var adaptive = settings.SizingMode == PaperSizingMode.LargestAdmissibleGridQuantity;
        var grid = ImmutableArray.Create(settings.FixedQuantity);
        if (adaptive && !PaperQuantityGrid.TryCreate(settings, out grid)) throw new ArgumentException("Invalid adaptive paper grid.");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal); var evaluated = 0;
        PaperSizingDecision Result(PaperSizingState state, PaperSizingCandidate? selected = null) => new(state, settings.SizingMode,
            selected?.Plan?.Quantity, evaluated, adaptive ? settings.MaximumQuantity!.Value : settings.FixedQuantity,
            adaptive ? settings.MinimumQuantity!.Value : settings.FixedQuantity, adaptive ? settings.QuantityStep!.Value : 0,
            [.. counts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new PaperSizingRejection(p.Key, p.Value))], selected?.Plan, selected?.Risk, at);
        if (adaptive && !budget.CanComplete(grid.Length)) return Result(PaperSizingState.EvaluationBudgetExceeded);
        try
        {
            foreach (var q in grid)
            {
                ct.ThrowIfCancellationRequested(); if (adaptive) budget.Consume(); evaluated++;
                var candidate = await evaluate(q, ct);
                if (candidate.Rejection is null && candidate.Plan is not null && candidate.Risk?.Decision == PaperRiskOutcome.Approved)
                    return Result(PaperSizingState.Selected, candidate);
                var reason = candidate.Rejection ?? "InputUnavailable"; counts[reason] = counts.GetValueOrDefault(reason) + 1;
                if (candidate.InputUnavailable) return Result(PaperSizingState.InputUnavailable);
            }
            return Result(PaperSizingState.NoAdmissibleQuantity);
        }
        catch (OverflowException) { return Result(PaperSizingState.ArithmeticOverflow); }
    }
    // Exclude random plan/fill GUIDs and timestamps from deterministic attempt identity.
    public static string PlanFingerprint(PaperPlan p) => PaperAutomationPolicy.Hash(new
    {
        p.Quantity, p.Cost, p.ExpectedPayoutAtResolution, p.ExpectedProfitAtResolution,
        Fills = p.Fills.Select(f => new { f.Instrument, f.Quantity, f.Price, f.Notional, f.Fee, f.Currency, f.Origin, f.NativeLiquidityIdentity, f.BookVersion }).ToArray()
    });
    public static string Fingerprint(PaperSizingProof p) => PaperAutomationPolicy.Hash(new
    {
        p.Version, p.Mode, p.SelectedQuantity, p.MinimumQuantity, p.MaximumQuantity, p.QuantityStep, p.CandidatesEvaluated, p.Rejections,
        p.TriggerInputStamp, p.AutomationProfileRevision, p.RiskPolicyRevision, p.GenerationId, p.FinancialRevision, p.PlanFingerprint,
        SessionDebits = p.SessionDebits.OrderBy(d => d.Exchange, StringComparer.Ordinal).ThenBy(d => d.Currency, StringComparer.Ordinal).ToArray()
    });
    public static PaperSizingProof CreateProof(PaperSizingDecision decision, PaperAutomationPermit permit, string stamp, PaperAutomationHistory history)
    {
        var p = new PaperSizingProof(2, decision.Mode, decision.SelectedQuantity!.Value, decision.LowestConfiguredQuantity, decision.HighestConfiguredQuantity,
            decision.QuantityStep, decision.CandidatesEvaluated, decision.Rejections, stamp, permit.Profile.Revision, permit.RiskRevision,
            permit.GenerationId, decision.SelectedRiskDecision!.FinancialRevision, PlanFingerprint(decision.SelectedPlan!), history.SessionDebits, "");
        return p with { DecisionFingerprint = Fingerprint(p) };
    }
    public static bool Healthy(PaperSizingProof p, PaperAutomationSettings settings, PaperPlan plan, string stamp, Guid profile, Guid risk, Guid generation, long financialRevision)
    {
        if (!PaperQuantityGrid.TryCreate(settings, out var grid)) return false;
        return p.Version == 2 && p.Mode == PaperSizingMode.LargestAdmissibleGridQuantity && p.Mode == settings.SizingMode && p.SelectedQuantity == plan.Quantity &&
            p.MinimumQuantity == settings.MinimumQuantity && p.MaximumQuantity == settings.MaximumQuantity && p.QuantityStep == settings.QuantityStep &&
            grid.Contains(p.SelectedQuantity) && p.CandidatesEvaluated == grid.IndexOf(p.SelectedQuantity) + 1 &&
            !p.Rejections.IsDefault && p.Rejections.Length <= PaperQuantityGrid.MaximumAdaptiveSizingCandidates && p.Rejections.All(r => r.Count > 0) &&
            p.Rejections.Sum(r => r.Count) == p.CandidatesEvaluated - 1 && !p.SessionDebits.IsDefault && p.SessionDebits.All(d => d.Notional >= 0 && d.Fees >= 0) &&
            p.TriggerInputStamp == stamp && p.AutomationProfileRevision == profile && p.RiskPolicyRevision == risk && p.GenerationId == generation &&
            p.FinancialRevision == financialRevision && p.PlanFingerprint == PlanFingerprint(plan) && p.DecisionFingerprint == Fingerprint(p);
    }
}
