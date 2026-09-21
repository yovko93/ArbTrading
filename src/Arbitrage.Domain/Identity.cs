namespace Arbitrage.Domain;

public enum TradingMode { Paper, Manual, Automatic }
public enum WorkspaceRole { Owner }

public static class Invariants
{
    public static Guid Id(Guid value) => value != Guid.Empty ? value : throw new ArgumentException("An identifier is required.");
    public static string DisplayName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Display name must contain 1–100 characters without control characters.");
        return name;
    }
}

public sealed class ApplicationUser
{
    private ApplicationUser() { }
    public ApplicationUser(Guid id, DateTimeOffset createdAt) { Id = Invariants.Id(id); CreatedAt = createdAt.ToUniversalTime(); }
    public Guid Id { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class Workspace
{
    private Workspace() { }
    public Workspace(Guid id, string displayName, DateTimeOffset createdAt)
    { Id = Invariants.Id(id); Rename(displayName); CreatedAt = createdAt.ToUniversalTime(); }
    public Guid Id { get; private set; }
    public string DisplayName { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public void Rename(string displayName) => DisplayName = Invariants.DisplayName(displayName);
}

public sealed class WorkspaceMembership
{
    private WorkspaceMembership() { }
    public WorkspaceMembership(Guid userId, Guid workspaceId)
    { UserId = Invariants.Id(userId); WorkspaceId = Invariants.Id(workspaceId); }
    public Guid UserId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public WorkspaceRole Role { get; private set; } = WorkspaceRole.Owner;
}

public sealed class LocalProfile
{
    private LocalProfile() { }
    public LocalProfile(Guid id, Guid userId, Guid defaultWorkspaceId)
    { Id = Invariants.Id(id); UserId = Invariants.Id(userId); DefaultWorkspaceId = Invariants.Id(defaultWorkspaceId); }
    public Guid Id { get; private set; }
    public int Slot { get; private set; } = 1;
    public Guid UserId { get; private set; }
    public Guid DefaultWorkspaceId { get; private set; }
}

public sealed record WorkspaceSettings(Guid WorkspaceId, string DisplayName);

public sealed class AuditRecord
{
    private AuditRecord() { }
    public AuditRecord(Guid actorId, Guid workspaceId, DateTimeOffset occurredAt, string correlationId)
    {
        Id = Guid.NewGuid(); ActorId = Invariants.Id(actorId); WorkspaceId = Invariants.Id(workspaceId);
        OccurredAt = occurredAt.ToUniversalTime();
        CorrelationId = !string.IsNullOrWhiteSpace(correlationId) && correlationId.Length <= 128
            ? correlationId : throw new ArgumentException("A bounded correlation identifier is required.");
    }
    public Guid Id { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string Action { get; private set; } = "Workspace.DisplayNameUpdated";
    public string CorrelationId { get; private set; } = "";
}
