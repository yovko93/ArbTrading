using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Arbitrage.Application;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class ApiTests
{
    [Fact]
    public async Task Only_minimal_liveness_is_anonymous()
    {
        await using var app = new BackendFixture(); using var client = app.CreateClient();
        Assert.Equal("{\"status\":\"Live\"}", await client.GetStringAsync("/health/live"));
        foreach (var path in new[] { "/system/status", "/session", "/exchanges/status", "/trading/mode", $"/workspaces/{Guid.NewGuid()}/settings" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/v1/workspaces/{Guid.NewGuid()}/settings", new { displayName = "Name" })).StatusCode);
    }

    [Fact]
    public async Task Invalid_credentials_and_identity_spoofing_cannot_authenticate()
    {
        await using var app = new BackendFixture(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/session?userId=" + Guid.NewGuid())).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/session")).StatusCode);
    }

    [Fact]
    public async Task Authenticated_contracts_report_actual_safe_capabilities()
    {
        await using var app = new BackendFixture(); using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var status = (await client.GetFromJsonAsync<SystemStatusResponse>("/api/v1/system/status"))!;
        var mode = (await client.GetFromJsonAsync<TradingModeResponse>("/api/v1/trading/mode"))!;
        var exchanges = (await client.GetFromJsonAsync<ExchangeStatusResponse[]>("/api/v1/exchanges/status"))!;
        Assert.NotEqual(Guid.Empty, session.UserId); Assert.NotEqual(Guid.Empty, session.DefaultWorkspaceId);
        Assert.Equal("Local", session.DeploymentMode); Assert.Equal("Healthy", status.PersistenceState);
        Assert.True(status.UptimeSeconds >= 0); Assert.NotEqual("unknown", status.BackendVersion);
        Assert.Equal("Paper", mode.ConfiguredMode); Assert.Equal("Paper", mode.EffectiveMode);
        Assert.Equal(Capabilities.Phase01A, mode.Capabilities); Assert.Equal(Capabilities.Phase01A, status.Capabilities);
        Assert.False(mode.Capabilities.PaperExecutionImplemented); Assert.False(mode.Capabilities.LiveOrderSubmissionAvailable);
        Assert.False(mode.Capabilities.ManualLiveExecutionAvailable); Assert.False(mode.Capabilities.AutomaticLiveExecutionAvailable);
        Assert.Equal(new[] { "Polymarket", "Kalshi" }, exchanges.Select(e => e.Exchange));
        Assert.All(exchanges, e => Assert.Equal("NotImplemented", e.IntegrationState));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PutAsJsonAsync("/api/v1/trading/mode", new { mode = "Automatic" })).StatusCode);
    }

    [Fact]
    public async Task Settings_write_is_persisted_and_audits_authenticated_actor_not_claimed_actor()
    {
        await using var app = new BackendFixture(); using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "forged");
        var response = await client.PutAsJsonAsync($"/api/v1/workspaces/{session.DefaultWorkspaceId}/settings", new UpdateWorkspaceSettingsRequest("  Research  "));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await client.GetFromJsonAsync<WorkspaceSettingsResponse>($"/api/v1/workspaces/{session.DefaultWorkspaceId}/settings");
        Assert.Equal("Research", actual!.DisplayName);
        var audit = await app.WithDatabaseAsync(db => db.AuditRecords.SingleAsync());
        Assert.Equal(session.UserId, audit.ActorId); Assert.Equal(session.DefaultWorkspaceId, audit.WorkspaceId);
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), audit.CorrelationId);
        Assert.NotEqual("forged", audit.CorrelationId); Assert.Equal(TimeSpan.Zero, audit.OccurredAt.Offset);
        Assert.Equal(session.UserId, (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session?userId=" + Guid.NewGuid()))!.UserId);
        var spoof = await client.PutAsJsonAsync($"/api/v1/workspaces/{session.DefaultWorkspaceId}/settings", new { displayName = "Forged", userId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, spoof.StatusCode);
        Assert.Equal("Research", (await client.GetFromJsonAsync<WorkspaceSettingsResponse>($"/api/v1/workspaces/{session.DefaultWorkspaceId}/settings"))!.DisplayName);
    }

    [Fact]
    public async Task Two_users_are_isolated_in_both_directions_and_rejected_writes_do_not_change_data()
    {
        await using var app = new BackendFixture(); using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var otherUser = Guid.NewGuid(); var otherWorkspace = Guid.NewGuid();
        await app.WithDatabaseAsync(async db =>
        {
            db.AddRange(new ApplicationUser(otherUser, DateTimeOffset.UtcNow), new Workspace(otherWorkspace, "Private", DateTimeOffset.UtcNow), new WorkspaceMembership(otherUser, otherWorkspace));
            return await db.SaveChangesAsync();
        });
        client.DefaultRequestHeaders.Add("X-User-Id", otherUser.ToString());
        var path = $"/api/v1/workspaces/{otherWorkspace}/settings";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(path, new UpdateWorkspaceSettingsRequest("Stolen"))).StatusCode);
        await app.WithDatabaseAsync(async db =>
        {
            var store = new LocalStore(db);
            Assert.Null(await store.ReadAsync(otherUser, session.DefaultWorkspaceId, default));
            Assert.False(await store.RenameAsync(otherUser, session.DefaultWorkspaceId, "Stolen", DateTimeOffset.UtcNow, "fixture", default));
            Assert.Equal("Private", (await store.ReadAsync(otherUser, otherWorkspace, default))!.DisplayName);
            Assert.Equal("Personal workspace", (await store.ReadAsync(session.UserId, session.DefaultWorkspaceId, default))!.DisplayName);
            Assert.Empty(await db.AuditRecords.ToListAsync());
            return true;
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nname")]
    public async Task Invalid_settings_leave_name_and_audit_unchanged(string name)
    {
        await using var app = new BackendFixture(); using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var path = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/settings";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new UpdateWorkspaceSettingsRequest(name))).StatusCode);
        Assert.Equal("Personal workspace", (await client.GetFromJsonAsync<WorkspaceSettingsResponse>(path))!.DisplayName);
        Assert.Equal(0, await app.WithDatabaseAsync(db => db.AuditRecords.CountAsync()));
    }
}
