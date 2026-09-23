using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed class FeeScheduleEntry
{
    public string Exchange { get; set; } = "";
    public string MarketId { get; set; } = "";
    public string ScheduleJson { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset RetrievedAt { get; set; }
}
public sealed class FeeProfileEntry
{
    public Guid Revision { get; set; }
    public Guid WorkspaceId { get; set; }
    public KalshiFeeAccountProfile Profile { get; set; }
}
// One bounded current bundle per catalog market, including bounded pending rules; no quote/tick history.
public sealed class FeeStore(TradingDbContext db) : IFeeStore
{
    public async Task<FeeSchedule?> ReadAsync(string exchange, string marketId, CancellationToken ct)
    {
        var row = await db.FeeSchedules.AsNoTracking().SingleOrDefaultAsync(x => x.Exchange == exchange && x.MarketId == marketId, ct);
        return row is null ? null : JsonSerializer.Deserialize<FeeSchedule>(row.ScheduleJson);
    }
    public async Task SaveAsync(FeeSchedule schedule, CancellationToken ct)
    {
        if (schedule.Rules.Length > 512 || schedule.Instruments.Length > 100) throw new ArgumentException("Fee metadata bound exceeded.");
        var row = await db.FeeSchedules.SingleOrDefaultAsync(x => x.Exchange == schedule.Exchange && x.MarketId == schedule.MarketId, ct);
        if (row is null) { row = new() { Exchange = schedule.Exchange, MarketId = schedule.MarketId }; db.FeeSchedules.Add(row); }
        row.ScheduleJson = JsonSerializer.Serialize(schedule); row.Fingerprint = schedule.Fingerprint; row.RetrievedAt = schedule.RetrievedAt;
        await db.SaveChangesAsync(ct);
    }
    public async Task<FeeProfileState> ReadProfileAsync(Guid workspace, CancellationToken ct)
    {
        var row = await db.FeeProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.WorkspaceId == workspace, ct);
        return new(row?.Profile ?? KalshiFeeAccountProfile.Unknown, row?.Revision ?? Guid.Empty);
    }
    public async Task<KalshiFeeAccountProfile> ProfileAsync(Guid workspace, CancellationToken ct) => (await ReadProfileAsync(workspace, ct)).Profile;
    public async Task SetProfileAsync(Guid workspace, KalshiFeeAccountProfile profile, CancellationToken ct)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentException("Invalid fee profile.");
        var row = await db.FeeProfiles.SingleOrDefaultAsync(p => p.WorkspaceId == workspace, ct);
        if (row is null) { row = new() { WorkspaceId = workspace }; db.FeeProfiles.Add(row); }
        row.Profile = profile; row.Revision = Guid.NewGuid(); await db.SaveChangesAsync(ct);
    }
}
