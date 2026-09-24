using System.Text;
using System.Text.Json;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Arbitrage.LocalTransport;

namespace Arbitrage.Backend;

public sealed class PaperReliabilityCoordinator(IServiceScopeFactory scopes, PaperReliabilityTelemetry telemetry, TimeProvider clock,
    RealtimePublisher publisher, LocalOptions options) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Guid backend = telemetry.BackendId;
    private readonly Dictionary<Guid, DateTimeOffset> nextEvaluation = [];
    private long persistenceFailures;
    public long TelemetryPersistenceFailures => Interlocked.Read(ref persistenceFailures);
    private async Task AttachAsync(PaperReliabilityStore store, PaperReliabilityCampaignEntry c, CancellationToken ct)
    {
        await store.RecoverAsync(c.Id, backend, ct);
        if (telemetry.Capture(c.WorkspaceId) is not null) return;
        c = (await store.ReadAsync(c.WorkspaceId, c.Id, ct))!;
        telemetry.Begin(c.WorkspaceId, c.Id, JsonSerializer.Deserialize<Dictionary<string, long>>(c.CountersJson)!, JsonSerializer.Deserialize<string[]>(c.TriggersJson)!);
    }
    public async Task ProcessOnceAsync(bool stopping = false, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var listing = scopes.CreateScope();
            var collecting = await listing.ServiceProvider.GetRequiredService<PaperReliabilityStore>().CollectingAsync(ct);
            var ids = collecting.Select(c => c.Id).ToHashSet();
            foreach (var old in nextEvaluation.Keys.Where(id => !ids.Contains(id)).ToArray()) nextEvaluation.Remove(old);
            foreach (var c in collecting)
            {
                try
                {
                    using var scope = scopes.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<PaperReliabilityStore>();
                    await AttachAsync(store, c, ct); var sample = telemetry.Capture(c.WorkspaceId, stopping)!;
                    await store.CheckpointAsync(c.WorkspaceId, backend, sample, stopping, "BackendStopped", ct);
                    if (!stopping && (!nextEvaluation.TryGetValue(c.Id, out var next) || next <= clock.GetUtcNow()))
                    { await store.EvaluateAsync(c.CreatedBy, c.WorkspaceId, c.Id, ct); nextEvaluation[c.Id] = clock.GetUtcNow().AddMinutes(5); publisher.PaperReliabilityChanged(c.WorkspaceId); }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { telemetry.Gap(c.WorkspaceId); Interlocked.Increment(ref persistenceFailures); }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { telemetry.GapAll(); Interlocked.Increment(ref persistenceFailures); }
        finally { gate.Release(); }
    }
    public async Task<PaperReliabilityCampaignEntry> ActAsync(Guid actor, Guid workspace, string action, Guid? id, Guid? revision, string? name, string? notes, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<PaperReliabilityStore>();
            if (action == "start")
            {
                var created = await store.StartAsync(actor, workspace, name ?? "", notes ?? "", backend, ct);
                telemetry.Begin(workspace, created.Id, [], []); publisher.PaperReliabilityChanged(workspace); return created;
            }
            var c = await store.ReadAsync(workspace, id ?? Guid.Empty, ct) ?? throw new ArgumentException("CampaignNotFound");
            if (c.Revision != revision) throw new ReliabilityConflict("RevisionConflict");
            if (action == "resume" && c.State != ReliabilityCampaignState.Paused) throw new ReliabilityConflict("NotPaused");
            if (action == "pause" && c.State != ReliabilityCampaignState.Collecting) throw new ReliabilityConflict("NotCollecting");
            if (c.State == ReliabilityCampaignState.Collecting)
            {
                await AttachAsync(store, c, ct); var sample = telemetry.Capture(workspace)!;
                var close = action is "pause" or "complete" or "cancel";
                await store.CheckpointAsync(workspace, backend, sample, close, action, ct, () => telemetry.Capture(workspace)!);
            }
            if (action is "evaluate" or "complete" or "cancel") await store.EvaluateAsync(actor, workspace, c.Id, ct);
            if (action != "evaluate")
            {
                c = await store.TransitionAsync(actor, workspace, c.Id, revision!.Value, action, backend, ct);
                if (action == "resume") telemetry.Begin(workspace, c.Id, JsonSerializer.Deserialize<Dictionary<string, long>>(c.CountersJson)!, JsonSerializer.Deserialize<string[]>(c.TriggersJson)!);
                else telemetry.Capture(workspace, true);
            }
            publisher.PaperReliabilityChanged(workspace); return c;
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException) { telemetry.Gap(workspace); Interlocked.Increment(ref persistenceFailures); throw; }
        finally { gate.Release(); }
    }
    public async Task<string> ExportAsync(Guid workspace, Guid campaign, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<PaperReliabilityStore>();
            if (await store.ReadAsync(workspace, campaign, ct) is null) throw new ArgumentException("CampaignNotFound");
            var evaluation = await store.LatestAsync(campaign, ct) ?? throw new ReliabilityConflict("EvaluateFirst");
            if (Encoding.UTF8.GetByteCount(evaluation.ReportJson) > 10_000_000) throw new ReliabilityConflict("ReportTooLarge");
            var directory = Path.Combine(options.DataDirectory, "reliability", workspace.ToString("N"), campaign.ToString("N"));
            ProtectedStorage.RejectLinks(directory); ProtectedStorage.CreatePrivateDirectory(directory);
            var target = Path.Combine(directory, "report.json"); var temp = Path.Combine(directory, "report.pending");
            ProtectedStorage.RejectLinks(target); ProtectedStorage.RejectLinks(temp);
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { await file.WriteAsync(Encoding.UTF8.GetBytes(evaluation.ReportJson), ct); await file.FlushAsync(ct); file.Flush(true); }
            ProtectedStorage.RestrictFile(temp); ProtectedStorage.RejectLinks(directory); ProtectedStorage.RejectLinks(target); File.Move(temp, target, true);
            return target;
        }
        finally { gate.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { while (!stoppingToken.IsCancellationRequested) { await ProcessOnceAsync(ct: stoppingToken); await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken); } }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken ct) { await base.StopAsync(ct); await ProcessOnceAsync(true, ct); }
}
