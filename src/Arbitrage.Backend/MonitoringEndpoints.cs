using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;

namespace Arbitrage.Backend;

public static class MonitoringEndpoints
{
    public static void MapMonitoring(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/monitoring");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapGet("/status", async (Guid workspaceId, IRequestActor actor, RelationshipStore members, MonitoringCoordinator monitor, CancellationToken ct) =>
        { await members.RequireMemberAsync(Actor(actor), workspaceId, false, ct); return Map(monitor.Status(workspaceId)); });
        group.MapGet("/profile", async (Guid workspaceId, IRequestActor actor, MonitoringStore store, CancellationToken ct) => Map(await store.ProfileAsync(Actor(actor), workspaceId, ct)));
        group.MapPut("/profile", async (Guid workspaceId, MonitoringProfileResponse request, IRequestActor actor, MonitoringStore store, MonitoringCoordinator monitor, CancellationToken ct) =>
        {
            var profile = Map(request); await monitor.SaveProfileAsync(Actor(actor), workspaceId, profile, ct);
            return Map(profile);
        });
        group.MapPost("/start", async (Guid workspaceId, IRequestActor actor, RelationshipStore members, MonitoringStore store, MonitoringCoordinator monitor, CancellationToken ct) =>
        {
            await members.RequireMemberAsync(Actor(actor), workspaceId, true, ct);
            var profile = await store.ProfileAsync(Actor(actor), workspaceId, ct); ct.ThrowIfCancellationRequested();
            try { return Results.Accepted(value: Map(monitor.StartMonitoring(Actor(actor), workspaceId, profile))); }
            catch (InvalidOperationException) { return Results.Conflict(new { Code = "MonitoringAlreadyActive" }); }
        });
        group.MapPost("/stop", async (Guid workspaceId, IRequestActor actor, RelationshipStore members, MonitoringCoordinator monitor, CancellationToken ct) =>
        { await members.RequireMemberAsync(Actor(actor), workspaceId, true, ct); return Map(monitor.StopMonitoring(Actor(actor), workspaceId)); });
        group.MapGet("/rankings", async (Guid workspaceId, int? page, int? pageSize, string? lane, string? strategy, string? exchange, string? trust,
            string? quality, string? feeStatus, string? sort, IRequestActor actor, MonitoringStore store, MonitoringCoordinator monitor, CancellationToken ct) =>
        {
            if (page is < 1 or > 10000 || pageSize is < 1 or > 100 || !Valid<RankingLane>(lane) || !Valid<OpportunityStrategy>(strategy) ||
                exchange is not (null or "Kalshi" or "Polymarket") || trust is not (null or "Deterministic" or "Manual") ||
                !Valid<OpportunityInputQuality>(quality) || !Valid<FeeStatus>(feeStatus) || sort is not null && !MonitoringProfile.Sorts.Contains(sort)) return Results.BadRequest();
            var profile = await store.ProfileAsync(Actor(actor), workspaceId, ct);
            var rows = (await monitor.RankingsAsync(Actor(actor), workspaceId, ct)).Where(x =>
                (lane is null || x.Lane.ToString() == lane) && (strategy is null || x.Result.Strategy.ToString() == strategy) &&
                (exchange is null || x.Result.Legs.Any(l => l.Instrument.Exchange == exchange)) && (trust is null || x.Result.RelationshipTrust.ToString() == trust) &&
                (quality is null || x.Result.InputQuality.ToString() == quality) && (feeStatus is null || x.Result.Fees?.Status.ToString() == feeStatus)).ToArray();
            var offset = ((page ?? 1) - 1) * (pageSize ?? 20);
            return Results.Ok(new MonitoringRankingPage(MonitoringRanking.Sort(rows, sort ?? profile.Sort).Skip(offset).Take(pageSize ?? 20).Select((r, i) => Map(r, offset + i + 1)).ToArray(), rows.Length, page ?? 1, pageSize ?? 20));
        });
        group.MapGet("/current/{key}", async (Guid workspaceId, string key, IRequestActor actor, MonitoringCoordinator monitor, CancellationToken ct) =>
        {
            if (key.Length != 64 || !key.All(char.IsAsciiHexDigit)) return Results.BadRequest();
            var row = (await monitor.RankingsAsync(Actor(actor), workspaceId, ct)).SingleOrDefault(x => x.Result.OpportunityKey == key);
            return row is null ? Results.NotFound() : Results.Ok(Map(row, 0));
        });
        group.MapGet("/alerts", async (Guid workspaceId, int? page, int? pageSize, IRequestActor actor, MonitoringStore store, CancellationToken ct) =>
        {
            var result = await store.AlertsAsync(Actor(actor), workspaceId, page ?? 1, pageSize ?? 20, ct);
            return new MonitoringAlertPage(result.Items.Select(x => new MonitoringAlertResponse(x.AlertId, x.TriggeredAt, x.Lane.ToString(), x.Reason, Map(x.Opportunity, 0))).ToArray(), result.Total, page ?? 1, pageSize ?? 20);
        });
    }
    private static Guid Actor(IRequestActor actor) => actor.UserId ?? throw new UnauthorizedAccessException();
    private static bool Valid<T>(string? value) where T : struct, Enum => value is null || Enum.GetNames<T>().Contains(value, StringComparer.Ordinal);
    public static MonitoringRankingResponse Map(MonitoredOpportunity row, int rank) => new(rank, row.Lane.ToString(), row.EvaluationGeneration, OpportunityEndpoints.Map(row.Result), row.BestEdge, row.RequiredEdge, row.Distance, row.AvailableQuantity, row.AlertState) { ProfileVersion = row.ProfileVersion };
    public static MonitoringProfile Map(MonitoringProfileResponse p) => new(p.RelationshipLimit, p.IncludeManualRelationships, p.MinimumGrossEdge, p.MinimumFeeAdjustedEdge, p.MaximumQuantity, p.MaximumSkewMilliseconds, p.NearEdgeWindow, p.Sort, p.EnableFeeAdjustedAlerts, p.EnableGrossOnlyAlerts, p.FeeAlertEdge, p.FeeAlertProfit, p.GrossAlertEdge, p.GrossAlertProfit, p.RearmHysteresis, p.CooldownSeconds, p.CsvEnabled, p.CsvIntervalSeconds, p.CsvTopRows, p.AlertRetentionCount, p.AlertRetentionDays);
    public static MonitoringProfileResponse Map(MonitoringProfile p) => new(p.RelationshipLimit, p.IncludeManualRelationships, p.MinimumGrossEdge, p.MinimumFeeAdjustedEdge, p.MaximumQuantity, p.MaximumSkewMilliseconds, p.NearEdgeWindow, p.Sort, p.EnableFeeAdjustedAlerts, p.EnableGrossOnlyAlerts, p.FeeAlertEdge, p.FeeAlertProfit, p.GrossAlertEdge, p.GrossAlertProfit, p.RearmHysteresis, p.CooldownSeconds, p.CsvEnabled, p.CsvIntervalSeconds, p.CsvTopRows, p.AlertRetentionCount, p.AlertRetentionDays);
    public static MonitoringStatusResponse Map(MonitorStatus s) => new(s.State.ToString(), s.StartedAt, s.StoppedAt, s.LastEvaluationAt,
        new(s.Coverage.ApprovedRelationshipsAvailable, s.Coverage.RelationshipsMonitored, s.Coverage.RelationshipsSkippedByBound, s.Coverage.CoveragePartial, s.Coverage.PlansBuilt, s.Coverage.PlansWithBooksAvailable, s.Coverage.PlansWithActionableBooks, s.Coverage.PlansWithResolvedFees, s.Coverage.FeeAdjustedOpportunities, s.Coverage.GrossOnlyOpportunities, s.Coverage.NearEdgeCandidates, s.Coverage.BlockedCandidates),
        s.DirtyQueueDepth, s.ReconciliationRequired, s.LastErrorCode, s.Generation, s.DirtyNotificationsReceived, s.DirtyNotificationsCoalesced, s.DirtyNotificationsDropped, s.ReconciliationPasses, s.EvaluationsStarted, s.EvaluationsCompleted, s.EvaluationFailures, s.RankingUpdates, s.AlertsRaised, s.AlertsSuppressedCooldown, s.AlertsSuppressedNotRearmed, s.CsvExportStatus, s.LastCsvErrorCode, s.CsvSnapshotsWritten, s.CsvWriteFailures);
}
