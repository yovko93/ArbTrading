using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Backend;
using Arbitrage.Execution;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class PaperRiskApiTests
{
    internal static readonly PaperRiskLimitsRequest Permissive = new(0, 1, 1, 1, 1, 1000, 1000, 1000, 1000, .001m, 0);
    internal static async Task<PaperRiskStatusResponse> Status(PaperApiTests.Case c) => (await c.Client.GetFromJsonAsync<PaperRiskStatusResponse>(c.Root + "/paper/admission-status"))!;
    internal static async Task<PaperRiskPolicyResponse> Save(PaperApiTests.Case c, PaperRiskLimitsRequest limits)
    {
        var prior = (await Status(c)).Policy;
        var response = await c.Client.PutAsJsonAsync(c.Root + "/paper/admission-policy", new SavePaperRiskPolicyRequest(prior?.Revision, true, limits));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<PaperRiskPolicyResponse>())!;
    }
    [Fact] public async Task Configuration_is_explicit_owner_confirmed_and_read_only_operations_do_not_fund_or_acquire()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(configureRisk: false);
        Assert.Equal("NotConfigured", (await Status(c)).State);
        var p = await c.Preview(); Assert.False(p.WouldExecute); Assert.False(p.RiskApproved);
        Assert.Equal("RiskPolicyNotConfigured", p.Rejection); Assert.NotEmpty(p.Fills);
        Assert.Equal("RiskPolicyNotConfigured", (await c.Execute(c.Request(p))).Rejection);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Client.PutAsJsonAsync(c.Root + "/paper/admission-policy", new SavePaperRiskPolicyRequest(null, false, Permissive))).StatusCode);
        var policy = await Save(c, Permissive); Assert.Equal("WithinLimits", (await Status(c)).State);
        var approved = await c.Preview(); Assert.True(approved.RiskApproved);
        Assert.All(approved.Debits, d => Assert.Equal(d.AvailableCash, approved.RiskDecision!.BucketAssessments.Single(b => b.Exchange == d.Exchange && b.Currency == d.Currency).AvailableCash));
        Assert.All((await c.Account()).Balances, b => Assert.Equal(b.InitialCash, b.AvailableCash));
        Assert.Equal(0, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
        using var anonymous = c.Fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(c.Root + "/paper/admission-policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(c.Root + "/paper/admission-policy", new SavePaperRiskPolicyRequest(policy.Revision, true, Permissive))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}/paper/admission-status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.PutAsJsonAsync($"/api/v1/workspaces/{Guid.NewGuid()}/paper/admission-policy", new SavePaperRiskPolicyRequest(null, true, Permissive))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Client.GetAsync(c.Root + $"/paper/admission-status?generationId={Guid.NewGuid()}")).StatusCode);
    }
    [Theory] [InlineData("quantity")] [InlineData("edge")] [InlineData("fractions")] [InlineData("count")]
    public async Task Invalid_profile_is_rejected_without_clamping(string field)
    {
        await using var c = new PaperApiTests.Case(); await c.Start(configureRisk: false);
        var limits = field switch { "quantity" => Permissive with { MaximumRequestedQuantity = 1001 }, "edge" => Permissive with { MinimumFeeAdjustedEdgePerShare = 0 },
            "fractions" => Permissive with { MinimumCashReserveFraction = 1 }, _ => Permissive with { MaximumOpenExecutions = 0 } };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Client.PutAsJsonAsync(c.Root + "/paper/admission-policy", new SavePaperRiskPolicyRequest(null, true, limits))).StatusCode);
        Assert.Equal("NotConfigured", (await Status(c)).State);
    }
    [Fact] public async Task Policy_revision_invalidates_preview_but_committed_request_recovery_precedes_new_admission()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var preview = await c.Preview();
        await Save(c, Permissive); Assert.Equal("RiskPolicyChanged", (await c.Execute(c.Request(preview))).Rejection);
        var fresh = await c.Preview(); var request = c.Request(fresh); var executed = await c.Execute(request); Assert.NotNull(executed.Execution);
        Assert.Equal(fresh.RiskDecision!.PolicyRevision, executed.Execution!.RiskPolicyRevision);
        await Save(c, Permissive with { MaximumOpenExecutions = 1, MaximumOpenExecutionsPerRelationship = 1 });
        c.Clock.Now += TimeSpan.FromMinutes(10); var retry = await c.Execute(request);
        Assert.True(retry.Duplicate); Assert.Equal(executed.Execution.Id, retry.Execution!.Id);
        Assert.Equal("DuplicateRequest", (await c.Execute(request with { Quantity = 11 })).Rejection);
        Assert.Equal("Healthy", (await (await c.Client.PostAsync(c.Root + $"/paper/reconcile?generationId={fresh.GenerationId}", null)).Content.ReadFromJsonAsync<PaperIntegrityResponse>())!.Integrity);
    }
    [Theory] [InlineData("cost")] [InlineData("reserve")] [InlineData("market")] [InlineData("executions")] [InlineData("relationship")]
    public async Task Concurrent_admission_never_overshoots_and_recalculates_inside_writer(string kind)
    {
        await using var c = new PaperApiTests.Case(); await c.Start();
        var limits = kind switch {
            "cost" => Permissive with { MaximumOpenCostBasisFraction = .08m, MaximumMarketCostBasisFraction = .08m, MaximumInstrumentCostBasisFraction = .08m },
            "reserve" => Permissive with { MinimumCashReserveFraction = .92m },
            "market" => Permissive with { MaximumMarketCostBasisFraction = .08m, MaximumInstrumentCostBasisFraction = .08m },
            "executions" => Permissive with { MaximumOpenExecutions = 1, MaximumOpenExecutionsPerRelationship = 1 },
            _ => Permissive with { MaximumOpenExecutionsPerRelationship = 1 } };
        await Save(c, limits); var a = await c.Preview(); var b = await c.Preview(); Assert.True(a.WouldExecute); Assert.True(b.WouldExecute);
        using var barrier = new Barrier(2);
        Task<PaperCommitResponse> Execute(PaperPreviewResponse p) => Task.Run(async () => { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); return await c.Execute(c.Request(p)); });
        var results = await Task.WhenAll(Execute(a), Execute(b)); Assert.Single(results, r => r.Execution is not null);
        var rejected = Assert.Single(results, r => r.Execution is null); Assert.Equal("RiskLimitExceeded", rejected.Rejection);
        Assert.NotEmpty(rejected.RiskDecision!.Violations);
        Assert.Equal(1, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperExecutionEntry>().CountAsync()));
        Assert.Equal(9.36792m, (await c.Client.GetFromJsonAsync<PaperPositionResponse[]>(c.Root + "/paper/positions"))!.Sum(p => p.CostBasis));
    }
    [Fact] public async Task Concurrent_policy_edits_use_expected_revision_and_audit_once()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var prior = (await Status(c)).Policy!;
        using var barrier = new Barrier(2);
        Task<HttpResponseMessage> SaveOne() => Task.Run(async () => { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); return await c.Client.PutAsJsonAsync(c.Root + "/paper/admission-policy", new SavePaperRiskPolicyRequest(prior.Revision, true, Permissive)); });
        var results = await Task.WhenAll(SaveOne(), SaveOne()); Assert.Single(results, r => r.IsSuccessStatusCode); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.NotEqual(prior.Revision, (await Status(c)).Policy!.Revision);
        Assert.Equal(1, await c.Fixture.WithDatabaseAsync(db => db.AuditRecords.CountAsync(a => a.Action == "PaperRiskPolicyUpdated")));
    }
    [Fact] public async Task Concurrent_distinct_instruments_cannot_exceed_materialized_position_count()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); await Save(c, Permissive with { MaximumOpenPositions = 2 });
        var p = await c.Preview(); var ticket = c.Fixture.Services.GetRequiredService<PaperPreviewCache>().Find(p.PreviewId, c.Session.UserId, c.Session.DefaultWorkspaceId)!;
        using var barrier = new Barrier(2);
        Task<PaperCommitResult> Run(bool other) => Task.Run(async () =>
        {
            await using var scope = c.Fixture.Services.CreateAsyncScope(); var store = scope.ServiceProvider.GetRequiredService<PaperStore>();
            var plan = ticket.Plan with { Id = Guid.NewGuid(), Fills = [.. ticket.Plan.Fills.Select(f => other && f.Instrument.Exchange == "Polymarket" ?
                f with { Instrument = f.Instrument with { NativeInstrumentId = "other-native-token" } } : f)] };
            var snapshot = await store.RiskSnapshotAsync(c.Session.DefaultWorkspaceId, ticket.Generation, default);
            var risk = PaperRiskEvaluator.Evaluate(snapshot.Profile, snapshot.State, plan, c.Clock.Now); Assert.Equal(PaperRiskOutcome.Approved, risk.Decision);
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            return await store.CommitAsync(c.Session.UserId, c.Session.DefaultWorkspaceId, ticket.Generation, Guid.NewGuid(), Guid.NewGuid().ToString(), plan, "position-barrier",
                _ => Task.FromResult(PaperRejection.None), commit => { commit(); return true; }, default, risk);
        });
        var results = await Task.WhenAll(Run(false), Run(true)); Assert.Single(results, r => r.Execution is not null);
        Assert.Contains(Assert.Single(results, r => r.Execution is null).RiskDecision!.Violations, v => v.Code == PaperRiskViolationCode.OpenPositionCountLimit);
        Assert.Equal(2, await c.Fixture.WithDatabaseAsync(db => db.Set<PaperPositionEntry>().CountAsync()));
    }
    [Fact] public async Task Tightening_is_diagnostic_until_new_entry_and_reset_preserves_policy()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var p = await c.Preview(); Assert.NotNull((await c.Execute(c.Request(p))).Execution);
        var before = (await c.Account()).Balances;
        var policy = await Save(c, Permissive with { MaximumMarketCostBasisFraction = .01m, MaximumInstrumentCostBasisFraction = .01m });
        Assert.Equal("OverLimit", (await Status(c)).State); Assert.False((await c.Preview()).WouldExecute);
        Assert.Equal(before, (await c.Account()).Balances);
        (await c.Client.GetAsync(c.Root + "/paper/valuation")).EnsureSuccessStatusCode();
        (await c.Client.PostAsJsonAsync(c.Root + "/paper/account/reset", new InitializePaperRequest(true, p.GenerationId, "Risk reset fixture", [new("Kalshi", "USD", 100)]))).EnsureSuccessStatusCode();
        Assert.Equal("WithinLimits", (await Status(c)).State); Assert.Equal(policy.Revision, (await Status(c)).Policy!.Revision);
        var historical = await c.Client.GetFromJsonAsync<PaperRiskStatusResponse>(c.Root + $"/paper/admission-status?generationId={p.GenerationId}"); Assert.Equal("OverLimit", historical!.State);
    }
    [Fact] public async Task Corrupt_profile_blocks_entry_without_silent_repair_and_owner_can_replace_it()
    {
        await using var c = new PaperApiTests.Case(); await c.Start();
        await c.Fixture.WithDatabaseAsync(async db => { var p = await db.Set<PaperRiskProfileEntry>().SingleAsync(); p.Fingerprint = "invalid"; return await db.SaveChangesAsync(); });
        Assert.Equal("IntegrityFailure", (await Status(c)).State); Assert.False((await c.Preview()).WouldExecute);
        await Save(c, Permissive); Assert.Equal("WithinLimits", (await Status(c)).State);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Settlement_and_valuation_remain_available_without_entry_admission(bool configured)
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await SettlementTests.Execute(c);
        if (configured) await Save(c, Permissive with { MaximumMarketCostBasisFraction = .01m, MaximumInstrumentCostBasisFraction = .01m });
        else await c.Fixture.WithDatabaseAsync(async db => { db.Remove(await db.Set<PaperRiskProfileEntry>().SingleAsync()); return await db.SaveChangesAsync(); });
        Assert.Equal(configured ? "OverLimit" : "NotConfigured", (await Status(c)).State);
        (await c.Client.GetAsync(c.Root + "/paper/valuation")).EnsureSuccessStatusCode();
        await SettlementTests.Settle(c, e.GenerationId);
        if (configured) Assert.Equal(1, (await Status(c)).Assessment.CurrentOpenExecutions); // PartiallySettled still consumes a slot.
        await SettlementTests.Settle(c, e.GenerationId, "Polymarket", "b", "123");
        Assert.Equal(configured ? "WithinLimits" : "NotConfigured", (await Status(c)).State);
        Assert.Equal(0, (await Status(c)).Assessment.CurrentOpenExecutions);
        Assert.Equal("Healthy", await SettlementTests.Reconcile(c, e.GenerationId));
    }
    [Fact] public async Task Additive_migration_preserves_every_baseline_table_and_legacy_settlement()
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await SettlementTests.Execute(c);
        await SettlementTests.Settle(c, e.GenerationId);
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            // This isolated fixture is reduced to the exact published 04C.1 schema before upgrade.
            await db.Database.MigrateAsync("20260923105901_PaperSettlement"); db.ChangeTracker.Clear();
            await db.Database.OpenConnectionAsync(); var connection = db.Database.GetDbConnection();
            var columns = new Dictionary<string, string[]>();
            // Explicitly inspect all application tables; exclude SQLite and EF internal tables.
            await using (var tables = connection.CreateCommand())
            {
                tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                await using var reader = await tables.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { var name = reader.GetString(0); if (!name.StartsWith("sqlite_", StringComparison.Ordinal) && !name.StartsWith("__", StringComparison.Ordinal)) columns.TryAdd(name, []); }
            }
            foreach (var table in columns.Keys.ToArray())
            {
                await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA table_info(\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\")";
                await using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(1)); columns[table] = names.ToArray();
            }
            async Task<string[]> Rows(string table)
            {
                string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                await using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT " + string.Join(",", columns[table].Select(Quote)) + " FROM " + Quote(table);
                await using var reader = await cmd.ExecuteReaderAsync(); var rows = new List<string>();
                while (await reader.ReadAsync()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(System.Text.Json.JsonSerializer.Serialize(values)); }
                return rows.Order(StringComparer.Ordinal).ToArray();
            }
            var before = new Dictionary<string, string[]>(); foreach (var table in columns.Keys) before[table] = await Rows(table);
            await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
            foreach (var table in columns.Keys) Assert.Equal(before[table], await Rows(table));
            var execution = await db.Set<PaperExecutionEntry>().SingleAsync(); Assert.Null(execution.RiskPolicyVersion); Assert.Null(execution.RiskPolicyRevision); Assert.Null(execution.RiskProofJson);
            Assert.Empty(await db.Set<PaperRiskProfileEntry>().ToArrayAsync()); Assert.False(db.Database.HasPendingModelChanges()); return 0;
        });
        Assert.Equal("NotConfigured", (await Status(c)).State);
        await SettlementTests.Settle(c, e.GenerationId, "Polymarket", "b", "123"); Assert.Equal("Healthy", await SettlementTests.Reconcile(c, e.GenerationId));
    }
    [Theory] [InlineData("version")] [InlineData("amount")] [InlineData("approval")]
    public async Task Reconciliation_checks_recorded_risk_proof_without_using_current_policy(string change)
    {
        await using var c = new PaperApiTests.Case(); await c.Start(); var e = await SettlementTests.Execute(c);
        await c.Fixture.WithDatabaseAsync(async db =>
        {
            var row = await db.Set<PaperExecutionEntry>().SingleAsync(); var proof = System.Text.Json.JsonSerializer.Deserialize<PaperRiskProof>(row.RiskProofJson!)!;
            proof = change switch { "amount" => proof with { Cost = proof.Cost + 1 }, "approval" => proof with { Decision = proof.Decision with { Decision = PaperRiskOutcome.Rejected } },
                _ => proof with { Decision = proof.Decision with { PolicyVersion = 99 } } };
            row.RiskProofJson = System.Text.Json.JsonSerializer.Serialize(proof); return await db.SaveChangesAsync();
        });
        Assert.Equal("Corrupt", await SettlementTests.Reconcile(c, e.GenerationId));
    }
}
