using System.Net;
using System.Net.Http.Json;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class OpportunityJobTests
{
    // Delay acquisition, but always delegate trust/fingerprint checks to the real SQLite provider.
    private sealed class Gate
    {
        public int Calls; public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class DelayedProvider(RelationshipStore store, Gate gate) : IRelationshipProvider
    {
        public Task<IReadOnlyList<ApprovedRelationship>> ReadApprovedAsync(Guid actor, Guid workspace, bool manual, CancellationToken ct) => store.ReadApprovedAsync(actor, workspace, manual, ct);
        public async Task<ApprovedRelationshipPage> ReadEvaluationPageAsync(Guid actor, Guid workspace, bool manual, Guid? id, string? exchange, int skip, int take, CancellationToken ct)
        {
            Interlocked.Increment(ref gate.Calls); gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(ct);
            return await store.ReadEvaluationPageAsync(actor, workspace, manual, id, exchange, skip, take, ct);
        }
    }
    [Theory] [InlineData("cancel", "Cancelled")] [InlineData("shutdown", "Cancelled")] [InlineData("budget", "Partial")] [InlineData("requestDisconnect", "Completed")]
    public async Task Backend_owned_job_obeys_cancellation_budget_and_duplicate_admission(string action, string expected)
    {
        var gate = new Gate();
        await using var f = new BackendFixture(s => s.AddScoped<IRelationshipProvider>(sp => new DelayedProvider(sp.GetRequiredService<RelationshipStore>(), gate)));
        using var client = await f.AuthenticatedClientAsync(); var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync());
        var root = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}/opportunities";
        Assert.Equal(0, gate.Calls); // Host startup never evaluates.
        using var requestLifetime = new CancellationTokenSource();
        using var response = await client.PostAsJsonAsync(root + "/evaluate", new EvaluateOpportunitiesRequest(RuntimeSeconds: action == "budget" ? 1 : 10), requestLifetime.Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var job = (await response.Content.ReadFromJsonAsync<OpportunityJobResponse>())!;
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/evaluate", new EvaluateOpportunitiesRequest())).StatusCode);
        var jobs = f.Services.GetRequiredService<OpportunityJobs>();
        if (action == "cancel") Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{root}/jobs/{job.Id}/cancel", null)).StatusCode);
        if (action == "shutdown") await jobs.StopAsync(default);
        if (action == "requestDisconnect") { requestLifetime.Cancel(); gate.Release.TrySetResult(); }
        job = await OpportunityApiTests.Finish(client, root, job); Assert.Equal(expected, job.State);
        Assert.NotNull(job.EndedAt); Assert.Equal(0, job.Results);
    }
    [Fact] public async Task Retention_is_bounded_and_evicted_runs_are_not_accessible()
    {
        await using var f = new BackendFixture(); using var client = await f.AuthenticatedClientAsync();
        var profile = await f.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync()); var root = $"/api/v1/workspaces/{profile.DefaultWorkspaceId}/opportunities";
        var first = await OpportunityApiTests.Start(client, root, new());
        for (var i = 0; i < 4; i++) await OpportunityApiTests.Start(client, root, new());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{root}/jobs/{first.Id}")).StatusCode);
    }
}
