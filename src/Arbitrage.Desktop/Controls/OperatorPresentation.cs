using System.Globalization;
using System.Windows.Data;
using Arbitrage.Contracts;

namespace Arbitrage.Desktop.Controls;

// Display-only mappings. Backend states, counts and decisions remain authoritative.
public sealed class OperatorToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Complete" or "Completed" or "Succeeded" or "VerifiedDeterministic" or "Continuous" or "RealtimeContinuous" or "Satisfied" or "CriteriaMet" or "FeeAdjusted" => "Good",
        "Running" or "Collecting" or "Streaming" or "Realtime" or "Snapshot" or "RestSnapshot" or "REST Snapshot" or "Fresh" or "FreshRest" or "Proposed" or "Candidate" or "GrossOnly" => "Info",
        "Partial" or "Cancelled" or "Interrupted" or "Stale" or "Resynchronizing" or "BestEffort" or "RealtimeBestEffort" or "VerifiedManual" or "Manual" or "NeedsReview" or "Unknown" or "Pending" or "Paused" or "InsufficientEvidence" or "CriteriaNotMet" or "NotSatisfied" or "NearEdge" or "Degraded" => "Warning",
        "Failed" or "Faulted" or "Rejected" or "Violated" or "InvariantViolation" or "Blocked" or "Gap" => "Error",
        _ => "Neutral"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class OperatorTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is DateTimeOffset at
        ? at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : parameter as string ?? "Unavailable";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class OperatorTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (parameter as string) switch
    {
        "Catalog" => value is ExchangeCatalogStatusResponse catalog ? CatalogMessage(catalog) : "Catalog status unavailable.",
        "BlockerTone" => value is RelationshipDetailResponse candidate && candidate.Evidence.Any(e => e.Blocking) ? "Warning" : "Neutral",
        "Blockers" => value is RelationshipDetailResponse detail
            ? detail.Evidence.Any(e => e.Blocking) ? "Review each blocking difference before downstream use."
                : "No blocking differences reported. Verification state remains authoritative."
            : "Select a candidate to read its blocking evidence. Verification is unavailable.",
        "Coverage" => value is MonitoringStatusResponse status ? CoverageMessage(status.Coverage) : "Monitoring coverage unavailable.",
        _ => "Unavailable"
    };
    private static string CatalogMessage(ExchangeCatalogStatusResponse catalog)
    {
        var cache = catalog.StoredMarkets > 0 ? "Stored cache remains available." : "No stored markets reported.";
        return catalog.LatestRun?.State switch
        {
            "Running" => $"{catalog.Exchange} catalog synchronization is running. Counters reflect the latest status read; total upstream size is unknown.",
            "Complete" => "Catalog complete for the last run's scope. Cached metadata is not an atomic snapshot.",
            "Failed" => $"Catalog synchronization failed. {cache} This attempt did not establish a complete catalog.",
            "Partial" => $"Catalog synchronization is partial. {cache} Review the current reason and scope.",
            "Cancelled" or "Interrupted" => $"Catalog synchronization was {catalog.LatestRun.State.ToLowerInvariant()}. {cache} This attempt did not establish a complete catalog.",
            _ => "No complete run is established by the latest status. Cache completeness is unverified."
        };
    }
    private static string CoverageMessage(MonitoringCoverageResponse coverage)
    {
        var notices = new List<string>();
        if (coverage.RelationshipsMonitored == 0) notices.Add("No relationships currently monitored.");
        if (coverage.PlansWithActionableBooks == 0) notices.Add("No actionable books.");
        if (coverage.FeeAdjustedOpportunities == 0) notices.Add("No fee-adjusted opportunities.");
        if (coverage.CoveragePartial) notices.Add("Coverage is partial; inspect skipped relationships and input counts.");
        return notices.Count == 0 ? "Coverage counters reflect the last backend evaluation. They do not establish overall system health." : string.Join(" ", notices);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
