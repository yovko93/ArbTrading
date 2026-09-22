using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Connectors;
using Arbitrage.Contracts;
using Arbitrage.Domain;
using Arbitrage.Infrastructure;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class RealtimeSecurityTests
{
    [Fact]
    public async Task Unsafe_file_permissions_and_relative_paths_are_rejected_without_modifying_source()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var app = new BackendFixture(); using var client = await app.AuthenticatedClientAsync();
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(app.Root, "unsafe.pem"); var pem = rsa.ExportPkcs8PrivateKeyPem();
        File.WriteAllText(path, pem); ProtectedStorage.RestrictFile(path);
        var file = new FileInfo(path); var acl = file.GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
            System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        file.SetAccessControl(acl);
        var store = app.Services.GetRequiredService<IExchangeCredentialStore>();
        Assert.Throws<CredentialOperationException>(() => store.Import("fixture-key-id", path, false, Guid.Empty));
        Assert.Throws<CredentialOperationException>(() => store.Import("fixture-key-id", "relative.pem", false, Guid.Empty));
        Assert.Equal(pem, File.ReadAllText(path)); Assert.False(store.Status().Configured);
    }
    [Fact]
    public void Unsupported_platform_never_reads_or_writes_a_secret_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new WindowsExchangeCredentialStore("/deliberately-unavailable/credential-test", TimeProvider.System);
        Assert.Equal("UnsupportedPlatform", store.Status().StoreCapability); Assert.Null(store.Open());
        Assert.Throws<CredentialOperationException>(() => store.Import("fixture-key-id", "/unused.pem", false, Guid.Empty));
    }
    private sealed class RejectFactory : IMarketWebSocketFactory
    {
        public int Calls;
        public IMarketWebSocket Create() { Calls++; throw new InvalidOperationException("Unexpected external socket"); }
    }
    [Fact]
    public async Task Credential_management_requires_owner_and_no_credentials_is_optional()
    {
        await using var app = new BackendFixture(); using var client = await app.AuthenticatedClientAsync(); using var anonymous = app.CreateClient();
        const string path = "/api/v1/local-runtime/kalshi-credentials";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path + "/import", new ImportCredentialRequest("fixture-id", "unused", false, Guid.Empty))).StatusCode);
        var status = (await client.GetFromJsonAsync<CredentialStatusResponse>(path))!;
        Assert.False(status.Configured); Assert.Equal("NotConfigured", status.LastAuthenticationResult);
        Assert.False(File.Exists(Path.Combine(app.Root, "backend", "credentials", "kalshi.dpapi")));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/remove", new RemoveCredentialRequest(false, Guid.Empty))).StatusCode);
    }
    [Fact]
    public async Task Workspace_membership_does_not_grant_credential_administration()
    {
        var other = Guid.NewGuid();
        await using var app = new BackendFixture(s => { s.RemoveAll<IRequestActor>(); s.AddScoped<IRequestActor>(_ => new OtherActor(other)); });
        using var client = await app.AuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/local-runtime/kalshi-credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/local-runtime/kalshi-credentials/remove", new RemoveCredentialRequest(true, Guid.Empty))).StatusCode);
    }
    private sealed class OtherActor(Guid id) : IRequestActor { public Guid? UserId => id; }
    [Fact]
    public async Task Unknown_instruments_and_unauthorized_workspaces_cannot_open_sockets()
    {
        var factory = new RejectFactory(); await using var app = new BackendFixture(s => { s.RemoveAll<IMarketWebSocketFactory>(); s.AddSingleton<IMarketWebSocketFactory>(factory); });
        using var client = await app.AuthenticatedClientAsync(); var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        var prefix = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/orderbooks/Kalshi/UNKNOWN";
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(prefix + "/realtime/start?instrumentId=yes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(prefix.Replace(session.DefaultWorkspaceId.ToString(), Guid.NewGuid().ToString()) + "/realtime/start", null)).StatusCode);
        Assert.Equal(0, factory.Calls);
    }
    [Fact]
    public async Task Missing_Kalshi_credentials_reports_authentication_required_and_depth_cannot_bypass_anchor()
    {
        await using var app = new BackendFixture(); using var client = await app.AuthenticatedClientAsync();
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/v1/session"))!;
        await app.WithDatabaseAsync(async db => { db.CatalogMarkets.Add(new() { Exchange = "Kalshi", NativeId = "TEST", Classification = "binary",
            OutcomesJson = """[{"Label":"Yes"},{"Label":"No"}]""" }); return await db.SaveChangesAsync(); });
        var prefix = $"/api/v1/workspaces/{session.DefaultWorkspaceId}/orderbooks/Kalshi/TEST";
        var cache = app.Services.GetRequiredService<OrderBookCache>(); var id = new OrderBookInstrumentId("Kalshi", "TEST", "yes", "Yes");
        cache.Store(OrderBookNormalizer.Normalize(id, [], [new(.5m, 10m, LiquidityOrigin.DerivedComplement)], DateTimeOffset.UtcNow));
        var started = await client.PostAsync(prefix + "/realtime/start?instrumentId=yes", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.False((await started.Content.ReadFromJsonAsync<OrderBookResponse>())!.IsActionable);
        OrderBookResponse? book = null;
        for (var i = 0; i < 100; i++) { book = await client.GetFromJsonAsync<OrderBookResponse>(prefix); if (book?.Realtime?.State == "AuthenticationRequired") break; await Task.Delay(20); }
        Assert.Equal("AuthenticationRequired", book!.Realtime!.State); Assert.False(book.IsActionable); Assert.NotNull(book.Snapshot);
        var preview = await client.PostAsJsonAsync(prefix + "/depth", new DepthPreviewRequest("yes", "Buy", 1, true));
        Assert.Equal(0m, (await preview.Content.ReadFromJsonAsync<GrossDepthResponse>())!.ExecutableQuantity);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(prefix + "/realtime/stop?instrumentId=yes", null)).StatusCode);
        Assert.Equal("Stopped", (await client.GetFromJsonAsync<OrderBookResponse>(prefix))!.Realtime!.State);
    }
    [Fact]
    public async Task Generated_key_DPAPI_import_replace_remove_and_API_do_not_expose_secret()
    {
        if (!OperatingSystem.IsWindows()) return; // Linux exercises fake stores; production explicitly reports unsupported.
        await using var app = new BackendFixture(); using var client = await app.AuthenticatedClientAsync();
        var source = Path.Combine(app.Root, "fixture-private.pem");
        using var rsa = RSA.Create(2048); var pem = rsa.ExportPkcs8PrivateKeyPem();
        File.WriteAllText(source, pem); ProtectedStorage.RestrictFile(source);
        const string endpoint = "/api/v1/local-runtime/kalshi-credentials";
        var imported = await client.PostAsJsonAsync(endpoint + "/import", new ImportCredentialRequest("fixture-key-1234", source, false, Guid.Empty));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var json = await imported.Content.ReadAsStringAsync(); var status = JsonSerializer.Deserialize<CredentialStatusResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(status.Configured); Assert.Equal("…1234", status.MaskedKeyId); Assert.DoesNotContain(pem, json); Assert.DoesNotContain("fixture-key-1234", json); Assert.DoesNotContain(source, json);
        var secretPath = Path.Combine(app.Root, "backend", "credentials", "kalshi.dpapi");
        ProtectedStorage.VerifyPrivateFile(secretPath);
        var encrypted = File.ReadAllBytes(secretPath); Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(encrypted));
        Assert.DoesNotContain(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()), Encoding.UTF8.GetString(encrypted));
        var denied = await client.PostAsJsonAsync(endpoint + "/import", new ImportCredentialRequest("fixture-key-1234", source, false, status.Version));
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); Assert.Equal(encrypted, File.ReadAllBytes(secretPath));
        using (var lease = app.Services.GetRequiredService<IExchangeCredentialStore>().Open())
        { Assert.NotNull(lease); using var verify = RSA.Create(); verify.ImportPkcs8PrivateKey(lease.PrivateKey, out _); Assert.Equal(rsa.ExportSubjectPublicKeyInfo(), verify.ExportSubjectPublicKeyInfo()); }
        var replaced = await client.PostAsJsonAsync(endpoint + "/import", new ImportCredentialRequest("fixture-key-5678", source, true, status.Version));
        var updated = (await replaced.Content.ReadFromJsonAsync<CredentialStatusResponse>())!; Assert.NotEqual(status.Version, updated.Version);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(secretPath)!, "*.tmp"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(endpoint + "/remove", new RemoveCredentialRequest(true, status.Version))).StatusCode);
        var removed = await client.PostAsJsonAsync(endpoint + "/remove", new RemoveCredentialRequest(true, updated.Version)); Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null(app.Services.GetRequiredService<IExchangeCredentialStore>().Open()); Assert.False(File.Exists(secretPath)); Assert.Equal(pem, File.ReadAllText(source));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(app.Root, "backend"), "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".lock", StringComparison.Ordinal)))
        {
            using var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(read); var content = await reader.ReadToEndAsync();
            Assert.DoesNotContain(pem, content); Assert.DoesNotContain(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()), content);
        }
    }
    [Theory] [InlineData("public")] [InlineData("malformed")] [InlineData("small")]
    public async Task Unsupported_key_material_is_rejected_without_replacing_original(string kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var app = new BackendFixture(); using var client = await app.AuthenticatedClientAsync();
        using var rsa = RSA.Create(kind == "small" ? 1024 : 2048);
        var source = Path.Combine(app.Root, "invalid.pem");
        File.WriteAllText(source, kind == "public" ? rsa.ExportSubjectPublicKeyInfoPem() : kind == "small" ? rsa.ExportPkcs8PrivateKeyPem() : new string('x', 50));
        ProtectedStorage.RestrictFile(source);
        var response = await client.PostAsJsonAsync("/api/v1/local-runtime/kalshi-credentials/import", new ImportCredentialRequest("fixture-key-id", source, false, Guid.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(app.Services.GetRequiredService<IExchangeCredentialStore>().Status().Configured);
    }
}
