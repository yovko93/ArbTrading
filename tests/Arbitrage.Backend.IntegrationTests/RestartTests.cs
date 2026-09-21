using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.LocalTransport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class RestartTests
{
    private sealed class RestartHost(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Local:DataDirectory", Path.Combine(root, "backend"));
            builder.UseSetting("Local:RuntimeDirectory", Path.Combine(root, "runtime"));
        }
    }

    [Fact]
    public async Task Backend_restart_preserves_ownership_and_settings_and_revokes_old_credential()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-tests", Guid.NewGuid().ToString("N"));
        var file = new ProtectedLocalConnectionFile(Path.Combine(root, "runtime"));
        SessionResponse session; LocalConnection first;
        try
        {
            await using (var host = new RestartHost(root))
            {
                using var client = host.CreateClient(); first = await ReadPublishedAsync(file, null);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Credential);
                session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
                var response = await client.PutAsJsonAsync($"/api/v1/workspaces/{session.DefaultWorkspaceId}/settings", new UpdateWorkspaceSettingsRequest("Restart persisted"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            await WaitForLeaseReleaseAsync(root);
            await using (var host = new RestartHost(root))
            {
                using var client = host.CreateClient(); var next = await ReadPublishedAsync(file, first.Credential);
                Assert.NotEqual(first.Credential, next.Credential);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Credential);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/session")).StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", next.Credential);
                Assert.Equal(session, await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"));
                Assert.Equal("Restart persisted", (await client.GetFromJsonAsync<WorkspaceSettingsResponse>($"/api/v1/workspaces/{session.DefaultWorkspaceId}/settings"))!.DisplayName);
            }
            await WaitForLeaseReleaseAsync(root);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<LocalConnection> ReadPublishedAsync(ILocalConnectionFile file, string? previous)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            try { var value = await file.ReadAsync(default); if (value.Credential != previous) return value; }
            catch (IOException) { }
            await Task.Delay(20);
        }
        throw new InvalidOperationException("Connection metadata was not published.");
    }

    private static async Task WaitForLeaseReleaseAsync(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { using var lease = new Arbitrage.Infrastructure.LocalRuntimeLease(Path.Combine(root, "backend"), Path.Combine(root, "runtime")); return; }
            catch (IOException) when (attempt < 200) { await Task.Delay(20); }
        }
    }
}
