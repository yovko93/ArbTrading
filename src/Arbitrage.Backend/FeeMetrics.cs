using System.Diagnostics.Metrics;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Backend;

// Fixed enum labels only: no market IDs, sources, credentials, or per-level logging.
public static class FeeMetrics
{
    private static readonly Meter Meter = new("Arbitrage.Fees", "1.0");
    private static readonly Counter<long> Quotes = Meter.CreateCounter<long>("fee_schedules_resolved");
    private static readonly Counter<long> Candidates = Meter.CreateCounter<long>("fee_opportunity_results");
    public static void Record(ArbitrageOpportunitySnapshot result)
    {
        if (result.Fees is not { } fees) return;
        foreach (var group in fees.Breakdown.GroupBy(q => (q.Context.Exchange, q.Context.MarketId)))
            Quotes.Add(1, new KeyValuePair<string, object?>("status", group.First().Status.ToString()));
        Candidates.Add(1, new KeyValuePair<string, object?>("state", fees.State.ToString()));
        if (result.GrossArbitrageExists) Candidates.Add(1, new KeyValuePair<string, object?>("state", "GrossCandidate"));
    }
}
