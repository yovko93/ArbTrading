using System.Net;
using System.Net.Http.Json;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class FeeApiTests
{
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact] public async Task Effective_boundary_invalidates_fees_without_network_or_gross_change()
    {
        var clock = new Clock(); var source = new Source();
        await using var f = new BackendFixture(s => { s.AddSingleton<TimeProvider>(clock); s.AddSingleton<IPublicFeeSource>(source); });
        using var client = await f.AuthenticatedClientAsync(); var id = await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic);
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}";
        clock.Now = DateTimeOffset.UtcNow;
        await f.WithDatabaseAsync(async db =>
        {
            var store = new FeeStore(db); var k = Schedule("Kalshi", "a") with { RetrievedAt = clock.Now };
            await store.SaveAsync(k with { Rules = k.Rules.Add(new("future", "quadratic", 2, clock.Now.AddSeconds(1), FeeSourceLevel.Series)) }, default);
            await store.SaveAsync(Schedule("Polymarket", "b") with { RetrievedAt = clock.Now }, default);
            await store.SetProfileAsync(profile.DefaultWorkspaceId, KalshiFeeAccountProfile.DirectMember, default); return 0;
        });
        OpportunityApiTests.Books(f, at: clock.Now);
        var job = await OpportunityApiTests.Start(client, root + "/opportunities", new(id, EvaluateFees: true));
        var before = Assert.Single((await client.GetFromJsonAsync<OpportunityPageResponse>($"{root}/opportunities/jobs/{job.Id}/results"))!.Items);
        clock.Now = clock.Now.AddSeconds(2);
        var after = (await client.GetFromJsonAsync<OpportunityResponse>($"{root}/opportunities/current/{before.OpportunityKey}"))!;
        Assert.Equal("FeeResultStale", after.Fees!.State); Assert.True(after.GrossArbitrageExists); Assert.Equal(before.GrossProfit, after.GrossProfit); Assert.Equal(0, source.Calls);
    }
    private sealed class Source : IPublicFeeSource
    {
        public int Calls; public bool Delay;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FeeSchedule> ReadAsync(string exchange, string marketId, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); if (Delay) await Release.Task.WaitAsync(ct); return Schedule(exchange, marketId); }
    }
    internal static FeeSchedule Schedule(string exchange, string id) => new(exchange, id, null, null, "USD", DateTimeOffset.UtcNow, null, "isolated authoritative fixture",
        [new("current", exchange == "Kalshi" ? "quadratic" : "prediction-quadratic-v1", exchange == "Kalshi" ? 1 : .04m, DateTimeOffset.UtcNow.AddMinutes(-1), FeeSourceLevel.Market)],
        exchange == "Kalshi" ? ["yes", "no"] : ["123", "456"]);
    private static async Task<FeeRefreshJobResponse> Finish(HttpClient client, string root, FeeRefreshJobResponse job)
    { for (var i = 0; job.State == "Running" && i < 200; i++) { await Task.Delay(20); job = (await client.GetFromJsonAsync<FeeRefreshJobResponse>($"{root}/jobs/{job.Id}"))!; } Assert.NotEqual("Running", job.State); return job; }
    [Theory] [InlineData("cancel", "Cancelled")] [InlineData("shutdown", "Cancelled")] [InlineData("budget", "Partial")] [InlineData("disconnect", "Completed")]
    public async Task Explicit_refresh_is_backend_owned_single_flight_and_bounded(string action, string expected)
    {
        var source = new Source { Delay = true }; await using var f = new BackendFixture(s => s.AddSingleton<IPublicFeeSource>(source));
        using var client = await f.AuthenticatedClientAsync(); await OpportunityApiTests.Seed(f);
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}/fees";
        Assert.Equal(0, source.Calls); using var disconnected = new CancellationTokenSource();
        var request = new RefreshFeesRequest([new("Kalshi", "a")], action == "budget" ? 1 : 10);
        using var response = await client.PostAsJsonAsync(root + "/refresh", request, disconnected.Token); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<FeeRefreshJobResponse>())!; await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/refresh", request)).StatusCode);
        if (action == "cancel") await client.PostAsync($"{root}/jobs/{job.Id}/cancel", null);
        if (action == "shutdown") await f.Services.GetRequiredService<FeeJobs>().StopAsync(default);
        if (action == "disconnect") { disconnected.Cancel(); source.Release.TrySetResult(); }
        job = await Finish(client, root, job); Assert.Equal(expected, job.State); Assert.Equal(action == "disconnect" ? 1 : 0, job.CompletedMarkets);
    }
    [Fact] public async Task Auth_membership_bounds_and_cached_reads_never_call_exchange()
    {
        var source = new Source(); await using var f = new BackendFixture(s => s.AddSingleton<IPublicFeeSource>(source));
        using var client = await f.AuthenticatedClientAsync(); using var anonymous = f.CreateClient();
        var id = await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic);
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}/fees";
        var request = new RefreshFeesRequest([new("Kalshi", "a")]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(root + "/refresh", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/workspaces/{Guid.NewGuid()}/fees/refresh", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/refresh", request with { RuntimeSeconds = 31 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(root + "/profile", new FeeProfileResponse("99"))).StatusCode);
        Assert.Equal("Unknown", (await client.GetFromJsonAsync<FeeProfileResponse>(root + "/profile"))!.Profile);
        Assert.Equal("ScheduleUnavailable", (await client.GetFromJsonAsync<FeeScheduleResponse>(root + "/schedules/Kalshi/a"))!.Status);
        await client.GetAsync($"/api/v1/workspaces/{profile.DefaultWorkspaceId}/snapshot");
        OpportunityApiTests.Books(f); var opportunities = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}/opportunities";
        var job = await OpportunityApiTests.Start(client, opportunities, new(id, EvaluateFees: true));
        var page = (await client.GetFromJsonAsync<OpportunityPageResponse>($"{opportunities}/jobs/{job.Id}/results?diagnostics=true"))!;
        Assert.Equal("FeeScheduleUnavailable", Assert.Single(page.Items).Fees!.State); Assert.Null(page.Items[0].Fees!.TotalExchangeFees);
        Assert.Empty((await client.GetFromJsonAsync<OpportunityPageResponse>($"{opportunities}/jobs/{job.Id}/results"))!.Items); Assert.Equal(0, source.Calls);
    }
    [Theory] [InlineData("profile")] [InlineData("profileRoundTrip")] [InlineData("schedule")] [InlineData("stale")]
    public async Task Retained_fee_results_invalidate_independently_of_gross(string change)
    {
        var source = new Source(); await using var f = new BackendFixture(s => s.AddSingleton<IPublicFeeSource>(source));
        using var client = await f.AuthenticatedClientAsync(); var id = await OpportunityApiTests.Seed(f, VerificationState.VerifiedDeterministic);
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}";
        await f.WithDatabaseAsync(async db => { var store = new FeeStore(db); await store.SaveAsync(Schedule("Kalshi", "a"), default); await store.SaveAsync(Schedule("Polymarket", "b"), default); return 0; });
        await client.PutAsJsonAsync(root + "/fees/profile", new FeeProfileResponse("NonDirectMember"));
        OpportunityApiTests.Books(f); var job = await OpportunityApiTests.Start(client, root + "/opportunities", new(id, EvaluateFees: true));
        var r = Assert.Single((await client.GetFromJsonAsync<OpportunityPageResponse>($"{root}/opportunities/jobs/{job.Id}/results"))!.Items);
        Assert.Equal("FeeAdjustedDetected", r.Fees!.State);
        if (change is "profile" or "profileRoundTrip")
        {
            await client.PutAsJsonAsync(root + "/fees/profile", new FeeProfileResponse("DirectMember"));
            if (change == "profileRoundTrip") await client.PutAsJsonAsync(root + "/fees/profile", new FeeProfileResponse("NonDirectMember"));
        }
        else await f.WithDatabaseAsync(async db => { var s = Schedule("Kalshi", "a"); await new FeeStore(db).SaveAsync(change == "stale" ? s with { RetrievedAt = DateTimeOffset.UtcNow.AddHours(-2) } : s with { VerificationIssue = "Changed metadata" }, default); return 0; });
        var current = (await client.GetFromJsonAsync<OpportunityResponse>($"{root}/opportunities/current/{r.OpportunityKey}"))!;
        Assert.Equal("FeeResultStale", current.Fees!.State); Assert.Null(current.Fees.TotalExchangeFees); Assert.Equal(r.GrossProfit, current.GrossProfit);
        Assert.True(current.GrossArbitrageExists); Assert.Equal(0, source.Calls);
    }
}
