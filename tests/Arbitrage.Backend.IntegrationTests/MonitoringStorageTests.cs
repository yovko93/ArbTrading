using System.Globalization;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Arbitrage.Strategies;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class MonitoringStorageTests
{
    [Fact] public async Task Csv_rejects_linked_output_directory_without_writing_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-tests", Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "monitoring"); var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
                using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
            }
            else Directory.CreateSymbolicLink(link, target);
            var csv = new MonitoringCsv(new() { DataDirectory = root }); await csv.SnapshotAsync(Guid.NewGuid(), [Row()], default);
            Assert.Equal("Failed", csv.Status); Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    internal static MonitoredOpportunity Row()
    {
        var at = DateTimeOffset.UtcNow; var a = new OrderBookInstrumentId("Polymarket", "a", "123456789012345678901234567890", "Yes"); var b = new OrderBookInstrumentId("Polymarket", "b", "2", "No");
        var relationship = new ApprovedRelationship(Guid.NewGuid(), RelationshipType.EquivalentOppositeOutcome, VerificationState.VerifiedDeterministic,
            [new(a.NativeInstrumentId, b.NativeInstrumentId, RelationshipType.EquivalentOppositeOutcome)], new(a.Exchange, a.NativeMarketId), new(b.Exchange, b.NativeMarketId), new(), new(), new());
        var plan = new OpportunityPlan(relationship, OpportunityStrategy.CrossMarketBuyBothComplements, a, b, DepthAction.Buy, DepthAction.Buy, true);
        CachedOrderBook Book(OrderBookInstrumentId id)
        { var book = OrderBookNormalizer.Normalize(id, [], [new(.4m, 10, LiquidityOrigin.NativeAsk)], at); return new(book, null, BookEligibility.Evaluate(book, at, TimeSpan.FromSeconds(5)), Version: 1); }
        return MonitoringRanking.Classify(MonitoringRanking.Compact(GrossOpportunityEvaluator.Evaluate(plan, Book(a), Book(b), new(), at)) with { SourceTitle = "  =danger,\"quoted\"\n" + new string('x', 500) }, 1, new());
    }
    [Theory] [InlineData("=SUM(A1)")] [InlineData(" +1")] [InlineData("-cmd")] [InlineData("@call")] [InlineData("\tplain")] [InlineData("\rplain")] [InlineData("\nplain")]
    public void Csv_text_neutralizes_formula_and_control_prefixes(string input) => Assert.StartsWith("\"'", MonitoringCsv.Cell(input));
    [Fact] public async Task Csv_is_invariant_atomic_bounded_rotated_and_failure_isolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-tests", Guid.NewGuid().ToString("N")); var workspace = Guid.NewGuid();
        try
        {
            var csv = new MonitoringCsv(new() { DataDirectory = root }); var row = Row();
            var previous = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); await csv.SnapshotAsync(workspace, Enumerable.Repeat(row, 200).ToArray(), default); }
            finally { CultureInfo.CurrentCulture = previous; }
            var directory = Path.Combine(root, "monitoring", workspace.ToString("N")); var path = Path.Combine(directory, "current-opportunities.csv");
            var contents = await File.ReadAllTextAsync(path); Assert.Contains("0.2", contents); Assert.Contains("\"'123456789012345678901234567890\"", contents);
            Assert.Contains("\"'  =danger,\"\"quoted\"\"", contents); Assert.DoesNotContain(new string('x', 500), contents);
            Assert.Equal(100, contents.Split(row.Result.OpportunityKey).Length - 1); Assert.False(File.Exists(Path.Combine(directory, "current-opportunities.pending")));
            var alert = new MonitoringAlert(Guid.NewGuid(), workspace, row.Result.OpportunityKey, DateTimeOffset.UtcNow, row.Lane, "fixture", row);
            for (var i = 0; i < 7; i++)
            {
                using (var file = new FileStream(Path.Combine(directory, "alerts.csv"), FileMode.Create)) file.SetLength(MonitoringCsv.MaximumBytes);
                await csv.AppendAlertAsync(alert, default);
            }
            Assert.Equal(5, Directory.GetFiles(directory, "alerts*.csv").Length); Assert.Equal("Healthy", csv.Status);
            Assert.All(Directory.GetFiles(directory, "alerts*.csv"), file => Assert.True(new FileInfo(file).Length <= MonitoringCsv.MaximumBytes));
            // Replacing a file with an open handle fails on Windows but can succeed on Unix.
            // A directory at the destination makes atomic file replacement fail on both platforms.
            File.Delete(path);
            Directory.CreateDirectory(path);
            try
            {
                await csv.SnapshotAsync(workspace, [row], default);
                Assert.Equal("Failed", csv.Status); Assert.Equal("CsvWriteUnavailable", csv.Error);
                Assert.Equal(1, csv.Failures); Assert.Equal(1, csv.Snapshots);
            }
            finally { Directory.Delete(path); }
            await csv.SnapshotAsync(workspace, [], default);
            Assert.Equal("Healthy", csv.Status); Assert.Null(csv.Error);
            Assert.Equal(1, csv.Failures); Assert.Equal(2, csv.Snapshots);
            Assert.True(File.Exists(path)); Assert.False(File.Exists(Path.Combine(directory, "current-opportunities.pending")));
            if (OperatingSystem.IsWindows())
            {
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    await csv.SnapshotAsync(workspace, [row], default);
                Assert.Equal("Failed", csv.Status); Assert.Equal("CsvWriteUnavailable", csv.Error);
                Assert.Equal(2, csv.Failures);
                await csv.SnapshotAsync(workspace, [], default);
                Assert.Equal("Healthy", csv.Status); Assert.Null(csv.Error);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task Phase03C_upgrade_preserves_existing_data_and_retention_never_deletes_audit()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "fixture.db"); var user = Guid.NewGuid(); var workspace = Guid.NewGuid(); var relationship = Guid.NewGuid(); var job = Guid.NewGuid(); var clock = new MonitoringApiTests.Clock();
        try
        {
            await using (var db = new TradingDbContext(DatabaseOptions.ForFile(path)))
            {
                await db.Database.MigrateAsync("20260922222855_VerifiedFeeSchedules");
                db.AddRange(new ApplicationUser(user, clock.Now), new Workspace(workspace, "Preserved", clock.Now), new WorkspaceMembership(user, workspace), new LocalProfile(Guid.NewGuid(), user, workspace));
                db.Add(new AuditRecord(user, workspace, clock.Now, "before-monitoring"));
                db.AddRange(RelationshipPersistenceTests.Market("Kalshi", "a"), RelationshipPersistenceTests.Market("Polymarket", "b"));
                db.MarketRelationships.Add(new() { Id = relationship, WorkspaceId = workspace, SourceExchange = "Kalshi", SourceId = "a", TargetExchange = "Polymarket", TargetId = "b", State = VerificationState.VerifiedManual });
                db.RelationshipOutcomeMappings.Add(new() { RelationshipId = relationship, SourceOutcomeId = "yes", TargetOutcomeId = "456", Type = RelationshipType.EquivalentOppositeOutcome });
                db.RelationshipEvidence.Add(new() { RelationshipId = relationship, Dimension = "fixture", Detail = "Preserve evidence" });
                db.RelationshipJobs.Add(new() { Id = job, ActorId = user, WorkspaceId = workspace, State = "Partial", StartedAt = clock.Now, Notice = "Preserved" });
                await db.SaveChangesAsync(); var fees = new FeeStore(db); await fees.SaveAsync(FeeApiTests.Schedule("Kalshi", "a"), default); await fees.SetProfileAsync(workspace, KalshiFeeAccountProfile.DirectMember, default);
            }
            await using (var db = new TradingDbContext(DatabaseOptions.ForFile(path)))
            {
                await db.Database.MigrateAsync(); Assert.Equal(user, (await db.Users.SingleAsync()).Id); Assert.Single(await db.LocalProfiles.ToArrayAsync());
                Assert.Single(await db.Memberships.ToArrayAsync()); Assert.Equal(2, await db.CatalogMarkets.CountAsync()); Assert.Equal(relationship, (await db.MarketRelationships.SingleAsync()).Id);
                Assert.Equal("456", (await db.RelationshipOutcomeMappings.SingleAsync()).TargetOutcomeId); Assert.Equal("Preserve evidence", (await db.RelationshipEvidence.SingleAsync()).Detail);
                Assert.Equal(job, (await db.RelationshipJobs.SingleAsync()).Id); Assert.Single(await db.FeeSchedules.ToArrayAsync()); Assert.Equal(KalshiFeeAccountProfile.DirectMember, await new FeeStore(db).ProfileAsync(workspace, default));
                var store = new MonitoringStore(db, new RelationshipStore(db, clock), clock); var profile = new MonitoringProfile(AlertRetentionCount: 2, AlertRetentionDays: 1);
                await store.SaveProfileAsync(user, workspace, profile, default);
                var row = Row();
                for (var i = 0; i < 5; i++) await store.AddAlertAsync(user, new(Guid.NewGuid(), workspace, row.Result.OpportunityKey, clock.Now.AddSeconds(i), row.Lane, "fixture", row), profile, default);
                Assert.Equal(2, (await store.AlertsAsync(user, workspace, 1, 20, default)).Total);
                clock.Now += TimeSpan.FromDays(2); Assert.Empty((await store.AlertsAsync(user, workspace, 1, 20, default)).Items);
                Assert.Contains(await db.AuditRecords.ToListAsync(), x => x.CorrelationId == "before-monitoring");
            }
            await using (var db = new TradingDbContext(DatabaseOptions.ForFile(path)))
            { Assert.Equal(2, (await new MonitoringStore(db, new RelationshipStore(db, clock), clock).ProfileAsync(user, workspace, default)).AlertRetentionCount); Assert.Empty(await db.MonitoringAlerts.ToListAsync()); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
