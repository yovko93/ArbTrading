using Arbitrage.Domain;

namespace Arbitrage.Application;

public enum Failure { None, Unauthenticated, NotFound, InvalidInput, Unavailable }
public sealed record Result<T>(T? Value, Failure Failure = Failure.None, string? Error = null)
{
    public bool IsSuccess => Failure == Failure.None;
    public static Result<T> Fail(Failure failure, string error) => new(default, failure, error);
}

public interface IRequestActor { Guid? UserId { get; } }

public interface IWorkspaceStore
{
    Task<WorkspaceSettings?> ReadAsync(Guid actorId, Guid workspaceId, CancellationToken cancellationToken);
    // Implementations must recheck membership in the write transaction, and persist audit atomically.
    Task<bool> RenameAsync(Guid actorId, Guid workspaceId, string name, DateTimeOffset now,
        string correlationId, CancellationToken cancellationToken);
}

public interface ILocalProfileStore
{
    Task<LocalProfile> GetAsync(CancellationToken cancellationToken);
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

public sealed class WorkspaceService(IRequestActor actor, IWorkspaceStore store, TimeProvider clock)
{
    public async Task<Result<WorkspaceSettings>> ReadAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (actor.UserId is not { } userId) return Result<WorkspaceSettings>.Fail(Failure.Unauthenticated, "Authentication required.");
        var settings = await store.ReadAsync(userId, workspaceId, cancellationToken);
        return settings is null ? Result<WorkspaceSettings>.Fail(Failure.NotFound, "Workspace unavailable.") : new(settings);
    }

    public async Task<Result<WorkspaceSettings>> RenameAsync(Guid workspaceId, string? name, string correlationId, CancellationToken cancellationToken)
    {
        if (actor.UserId is not { } userId) return Result<WorkspaceSettings>.Fail(Failure.Unauthenticated, "Authentication required.");
        string normalized;
        try { normalized = Invariants.DisplayName(name); }
        catch (ArgumentException) { return Result<WorkspaceSettings>.Fail(Failure.InvalidInput, "Display name must contain 1–100 characters without control characters."); }
        var saved = await store.RenameAsync(userId, workspaceId, normalized, clock.GetUtcNow(), correlationId, cancellationToken);
        return saved ? new(new(workspaceId, normalized)) : Result<WorkspaceSettings>.Fail(Failure.NotFound, "Workspace unavailable.");
    }
}
