using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Application.Tests;

public sealed class WorkspaceServiceTests
{
    private sealed class Actor(Guid? userId) : IRequestActor { public Guid? UserId => userId; }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero); }
    private sealed class Store : IWorkspaceStore
    {
        public bool Authorized { get; set; } = true;
        public int Calls { get; private set; }
        public (Guid Actor, Guid Workspace, string Name, DateTimeOffset At, string Correlation)? Write { get; private set; }
        public Task<WorkspaceSettings?> ReadAsync(Guid actorId, Guid workspaceId, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Authorized ? new WorkspaceSettings(workspaceId, "Personal") : null); }
        public Task<bool> RenameAsync(Guid actorId, Guid workspaceId, string name, DateTimeOffset now, string correlationId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; Write = (actorId, workspaceId, name, now, correlationId); return Task.FromResult(Authorized); }
    }

    [Fact]
    public async Task Unauthenticated_actor_never_reaches_storage()
    {
        var store = new Store(); var service = new WorkspaceService(new Actor(null), store, new Clock());
        Assert.Equal(Failure.Unauthenticated, (await service.ReadAsync(Guid.NewGuid(), default)).Failure);
        Assert.Equal(Failure.Unauthenticated, (await service.RenameAsync(Guid.NewGuid(), "Name", "c", default)).Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Invalid_input_cannot_write()
    {
        var store = new Store(); var service = new WorkspaceService(new Actor(Guid.NewGuid()), store, new Clock());
        Assert.Equal(Failure.InvalidInput, (await service.RenameAsync(Guid.NewGuid(), "\t", "c", default)).Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Authorized_actor_and_clock_are_passed_to_atomic_write()
    {
        var actor = Guid.NewGuid(); var workspace = Guid.NewGuid(); var store = new Store(); var clock = new Clock();
        var result = await new WorkspaceService(new Actor(actor), store, clock).RenameAsync(workspace, " New name ", "correlation", default);
        Assert.True(result.IsSuccess);
        Assert.Equal((actor, workspace, "New name", clock.GetUtcNow(), "correlation"), store.Write);
    }

    [Fact]
    public async Task Unauthorized_and_missing_workspaces_are_indistinguishable()
    {
        var service = new WorkspaceService(new Actor(Guid.NewGuid()), new Store { Authorized = false }, new Clock());
        Assert.Equal(Failure.NotFound, (await service.ReadAsync(Guid.NewGuid(), default)).Failure);
        Assert.Equal(Failure.NotFound, (await service.RenameAsync(Guid.NewGuid(), "Name", "c", default)).Failure);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        var service = new WorkspaceService(new Actor(Guid.NewGuid()), new Store(), new Clock());
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RenameAsync(Guid.NewGuid(), "Name", "c", new CancellationToken(true)));
    }
}
