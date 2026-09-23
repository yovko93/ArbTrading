using Arbitrage.Execution;
namespace Arbitrage.Backend;

public sealed class PaperRiskDiagnostics
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Dictionary<string, long>> counters = [];
    public void Increment(Guid workspace, string name)
    {
        lock (gate)
        {
            if (!counters.TryGetValue(workspace, out var values))
            { if (counters.Count >= 128) return; counters[workspace] = values = []; }
            var old = values.GetValueOrDefault(name); values[name] = old == long.MaxValue ? old : old + 1;
        }
    }
    public void Record(Guid workspace, PaperRiskDecision? d)
    {
        if (d is null) return;
        Increment(workspace, "PaperRiskEvaluations"); Increment(workspace, d.Decision == PaperRiskOutcome.Approved ? "PaperRiskApproved" : "PaperRiskRejected");
        if (d.Violations.Any(v => v.Code == PaperRiskViolationCode.MinimumCashReserve)) Increment(workspace, "PaperRiskReserveRejected");
        if (d.Violations.Any(v => v.Code is PaperRiskViolationCode.TotalOpenCostBasisLimit or PaperRiskViolationCode.MarketCostBasisLimit or PaperRiskViolationCode.InstrumentCostBasisLimit)) Increment(workspace, "PaperRiskExposureRejected");
        if (d.Violations.Any(v => v.Code is PaperRiskViolationCode.OpenPositionCountLimit or PaperRiskViolationCode.OpenExecutionCountLimit or PaperRiskViolationCode.RelationshipExecutionCountLimit)) Increment(workspace, "PaperRiskCountRejected");
    }
    public IReadOnlyDictionary<string, long> Read(Guid workspace) { lock (gate) return counters.TryGetValue(workspace, out var c) ? new Dictionary<string, long>(c) : new Dictionary<string, long>(); }
}
