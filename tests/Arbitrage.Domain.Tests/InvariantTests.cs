using Arbitrage.Domain;

namespace Arbitrage.Domain.Tests;

public sealed class InvariantTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    [InlineData(null)]
    public void Invalid_names_are_rejected(string? name) => Assert.Throws<ArgumentException>(() => Invariants.DisplayName(name));

    [Fact]
    public void Names_are_trimmed_and_bounded()
    {
        var workspace = new Workspace(Guid.NewGuid(), "  Personal  ", DateTimeOffset.Now);
        Assert.Equal("Personal", workspace.DisplayName);
        Assert.Throws<ArgumentException>(() => workspace.Rename(new string('x', 101)));
        Assert.Equal("Personal", workspace.DisplayName);
    }

    [Fact]
    public void Empty_identifiers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ApplicationUser(Guid.Empty, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new WorkspaceMembership(Guid.NewGuid(), Guid.Empty));
        Assert.Throws<ArgumentException>(() => new LocalProfile(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Instants_are_normalized_and_audit_is_explicit()
    {
        var instant = new DateTimeOffset(2026, 9, 21, 15, 12, 11, TimeSpan.FromHours(3));
        var actor = Guid.NewGuid(); var workspace = Guid.NewGuid();
        var audit = new AuditRecord(actor, workspace, instant, "correlation");
        Assert.Equal(TimeSpan.Zero, audit.OccurredAt.Offset);
        Assert.Equal(instant.UtcTicks, audit.OccurredAt.UtcTicks);
        Assert.Equal(actor, audit.ActorId); Assert.Equal(workspace, audit.WorkspaceId);
        Assert.Equal("Workspace.DisplayNameUpdated", audit.Action);
        Assert.Throws<ArgumentException>(() => new AuditRecord(actor, workspace, instant, ""));
    }

    [Fact]
    public void Trading_modes_are_represented() => Assert.Equal(new[] { TradingMode.Paper, TradingMode.Manual, TradingMode.Automatic }, Enum.GetValues<TradingMode>());
}
