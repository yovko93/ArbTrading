using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Arbitrage.Backend.IntegrationTests;
public sealed class RelationshipJobTests
{
    private sealed class PausedClock : TimeProvider, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public override long GetTimestamp() { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return base.GetTimestamp(); }
        public void Dispose() => Release.Dispose();
    }
    [Fact]
    public async Task Backend_job_duplicate_admission_and_running_cancellation_are_enforced()
    {
        await using var fixture = new BackendFixture(); using var client = await fixture.AuthenticatedClientAsync();
        var profile = await fixture.WithDatabaseAsync(db => db.LocalProfiles.SingleAsync());
        using var clock = new PausedClock(); using var jobs = new RelationshipJobs(fixture.Services.GetRequiredService<IServiceScopeFactory>(), clock);
        await ((IHostedService)jobs).StartAsync(default);
        try
        {
            var job = await jobs.StartAsync(profile.UserId, profile.DefaultWorkspaceId, new GenerateRelationshipsRequest(), default);
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<RelationshipConflictException>(() => jobs.StartAsync(profile.UserId, profile.DefaultWorkspaceId, new GenerateRelationshipsRequest(), default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => jobs.CancelAsync(Guid.NewGuid(), profile.DefaultWorkspaceId, job.Id, default));
            await jobs.CancelAsync(profile.UserId, profile.DefaultWorkspaceId, job.Id, default); clock.Release.Set();
            var status = "Running";
            for (var i = 0; i < 100 && status == "Running"; i++) { await Task.Delay(20); status = await fixture.WithDatabaseAsync(db => db.RelationshipJobs.Where(j => j.Id == job.Id).Select(j => j.State).SingleAsync()); }
            Assert.Equal("Cancelled", status);
        }
        finally { clock.Release.Set(); await jobs.StopAsync(default); }
    }
}
