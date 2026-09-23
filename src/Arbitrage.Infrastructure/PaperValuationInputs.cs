using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed record PaperValuationInputs(PaperGenerationEntry Generation, PaperPositionEntry[] Positions, PaperBalanceEntry[] Balances,
    MarketCatalogEntry[] Markets, FeeSchedule[] Fees, FeeProfileState Profile);
public sealed partial class PaperStore
{
    public Task<Guid?> PositionGenerationAsync(Guid workspace, Guid position, CancellationToken ct) =>
        (from p in db.Set<PaperPositionEntry>().AsNoTracking() join g in db.Set<PaperGenerationEntry>().AsNoTracking() on p.GenerationId equals g.Id
         where p.Id == position && g.WorkspaceId == workspace select (Guid?)g.Id).SingleOrDefaultAsync(ct);
    // One short deferred SQLite read snapshot. Dispose it BEFORE acquiring cache locks/calculating depth.
    // Reject oversized generations explicitly; never summarize a silently truncated portfolio.
    public async Task<PaperValuationInputs?> ValuationInputsAsync(Guid workspace, Guid? generation, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var scope = await db.Database.UseTransactionAsync(transaction, ct);
        var g = generation is { } id ? await GenerationAsync(workspace, id, ct) : await ActiveAsync(workspace, ct);
        if (g is null) return null;
        var positions = await db.Set<PaperPositionEntry>().AsNoTracking().Where(p => p.GenerationId == g.Id).OrderBy(p => p.Id).Take(1001).ToArrayAsync(ct);
        if (positions.Length > 1000) throw new ArgumentException("ValuationPositionLimitExceeded");
        var balances = await BalancesAsync(g.Id, ct);
        var ids = positions.Select(p => p.MarketId).Distinct().ToArray();
        var markets = await db.CatalogMarkets.AsNoTracking().Where(m => ids.Contains(m.NativeId)).ToArrayAsync(ct);
        var fees = await db.FeeSchedules.AsNoTracking().Where(f => ids.Contains(f.MarketId)).ToArrayAsync(ct);
        var profile = await new FeeStore(db).ReadProfileAsync(workspace, ct);
        return new(g, positions, balances, markets, fees.Select(f => JsonSerializer.Deserialize<FeeSchedule>(f.ScheduleJson)!).ToArray(), profile);
    }
}
