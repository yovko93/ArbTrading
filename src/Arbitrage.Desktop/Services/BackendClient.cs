using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitrage.Contracts;
using Arbitrage.LocalTransport;

namespace Arbitrage.Desktop.Services;

public enum ConnectionState { Loading, Connected, Unavailable, AuthenticationFailed, AuthorizationDenied, Disconnected }
public sealed class BackendFailure(ConnectionState state, string message) : Exception(message)
{
    public ConnectionState State { get; } = state;
}
public sealed record BackendSnapshot(SessionResponse Session, SystemStatusResponse System,
    WorkspaceSettingsResponse Workspace, ExchangeStatusResponse[] Exchanges, string Endpoint);

public sealed class BackendClient(HttpClient http, ILocalConnectionFile connections)
{
    public Task<OrderBookResponse> OrderBookAsync(Guid workspace, string exchange, string market, string? instrument,
        bool refresh, CancellationToken ct) => SendAsync<OrderBookResponse>(refresh ? HttpMethod.Post : HttpMethod.Get,
            $"api/v1/workspaces/{workspace}/orderbooks/{Uri.EscapeDataString(exchange)}/{Uri.EscapeDataString(market)}" +
            (refresh ? "/refresh" : "") + (instrument is null ? "" : "?instrumentId=" + Uri.EscapeDataString(instrument)), null, ct);
    public string? LastEndpoint { get; private set; }
    public Task<LocalConnection> ReadConnectionAsync(CancellationToken cancellationToken) => connections.ReadAsync(cancellationToken);
    public Task<SessionResponse> GetSessionAsync(CancellationToken cancellationToken) =>
        SendAsync<SessionResponse>(HttpMethod.Get, "api/v1/session", null, cancellationToken);
    public Task<ApplicationSnapshotResponse> LoadSnapshotAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        SendAsync<ApplicationSnapshotResponse>(HttpMethod.Get, $"api/v1/workspaces/{workspaceId}/snapshot", null, cancellationToken);
    public Task<RecentDiagnosticsResponse> RecentDiagnosticsAsync(Guid workspaceId, long after, CancellationToken cancellationToken) =>
        SendAsync<RecentDiagnosticsResponse>(HttpMethod.Get, $"api/v1/workspaces/{workspaceId}/diagnostics?after={after}&take=100", null, cancellationToken);
    public Task<StopLocalRuntimeResponse> StopLocalRuntimeAsync(Guid instanceId, CancellationToken cancellationToken) =>
        SendAsync<StopLocalRuntimeResponse>(HttpMethod.Post, "api/v1/local-runtime/stop", new StopLocalRuntimeRequest(instanceId), cancellationToken);
    public Task<CatalogStatusResponse> CatalogStatusAsync(Guid workspaceId, CancellationToken ct) =>
        SendAsync<CatalogStatusResponse>(HttpMethod.Get, $"api/v1/workspaces/{workspaceId}/catalog/status", null, ct);
    public Task<MarketPageResponse> CatalogMarketsAsync(Guid workspaceId, string? exchange, string? search,
        string? status, string? tag, string sort, int page, int pageSize, CancellationToken ct)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}", "sort=" + Uri.EscapeDataString(sort) };
        if (exchange is not null) query.Add("exchange=" + Uri.EscapeDataString(exchange));
        if (!string.IsNullOrWhiteSpace(search)) query.Add("search=" + Uri.EscapeDataString(search));
        if (status is not null) query.Add("status=" + Uri.EscapeDataString(status));
        if (tag is not null) query.Add("tag=" + Uri.EscapeDataString(tag));
        return SendAsync<MarketPageResponse>(HttpMethod.Get,
            $"api/v1/workspaces/{workspaceId}/catalog/markets?{string.Join("&", query)}", null, ct);
    }
    public Task<MarketResponse> CatalogMarketAsync(Guid workspaceId, string exchange, string nativeId, CancellationToken ct) =>
        SendAsync<MarketResponse>(HttpMethod.Get,
            $"api/v1/workspaces/{workspaceId}/catalog/markets/{Uri.EscapeDataString(exchange)}/{Uri.EscapeDataString(nativeId)}", null, ct);
    public Task<StartMarketSyncResponse> StartMarketSyncAsync(Guid workspaceId, string exchange, CancellationToken ct) =>
        SendAsync<StartMarketSyncResponse>(HttpMethod.Post, $"api/v1/workspaces/{workspaceId}/catalog/sync",
            new StartMarketSyncRequest(exchange), ct);
    public Task<DiscoveryRunResponse> CancelMarketSyncAsync(Guid workspaceId, Guid runId, CancellationToken ct) =>
        SendAsync<DiscoveryRunResponse>(HttpMethod.Post,
            $"api/v1/workspaces/{workspaceId}/catalog/sync/{runId}/cancel", null, ct);
    public async Task<BackendSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var session = await SendAsync<SessionResponse>(HttpMethod.Get, "api/v1/session", null, cancellationToken);
        var status = await SendAsync<SystemStatusResponse>(HttpMethod.Get, "api/v1/system/status", null, cancellationToken);
        var workspace = await SendAsync<WorkspaceSettingsResponse>(HttpMethod.Get, $"api/v1/workspaces/{session.DefaultWorkspaceId}/settings", null, cancellationToken);
        var exchanges = await SendAsync<ExchangeStatusResponse[]>(HttpMethod.Get, "api/v1/exchanges/status", null, cancellationToken);
        return new(session, status, workspace, exchanges, LastEndpoint ?? "Unavailable");
    }

    public Task<WorkspaceSettingsResponse> RenameAsync(Guid workspaceId, string displayName, CancellationToken cancellationToken) =>
        SendAsync<WorkspaceSettingsResponse>(HttpMethod.Put, $"api/v1/workspaces/{workspaceId}/settings", new UpdateWorkspaceSettingsRequest(displayName), cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            // Each operation reads current metadata. Only safe reads may retry after rotation.
            var retryOnUnauthorized = method == HttpMethod.Get;
            for (var attempt = 0; attempt < (retryOnUnauthorized ? 2 : 1); attempt++)
            {
                var connection = await connections.ReadAsync(cancellationToken);
                using var request = new HttpRequestMessage(method, new Uri(new Uri(connection.BaseUrl + "/"), path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Credential);
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized && retryOnUnauthorized && attempt == 0) continue;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new BackendFailure(ConnectionState.AuthenticationFailed, "Local authentication failed. Refresh after verifying the backend runtime directory.");
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new BackendFailure(ConnectionState.AuthorizationDenied, "Access to this workspace was denied by the backend.");
                if (response.StatusCode == HttpStatusCode.BadRequest)
                    throw new BackendFailure(ConnectionState.Unavailable, "The display name must contain 1–100 characters without control characters.");
                if (!response.IsSuccessStatusCode)
                    throw new BackendFailure(ConnectionState.Unavailable, "The requested backend operation is unavailable.");
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken) ?? throw new JsonException();
                LastEndpoint = connection.BaseUrl;
                return value;
            }
            throw new BackendFailure(ConnectionState.AuthenticationFailed, "Local authentication failed.");
        }
        catch (HttpRequestException) { throw new BackendFailure(ConnectionState.Disconnected, "Backend disconnected. Use Local Backend Start or Refresh to check again."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new BackendFailure(ConnectionState.Disconnected, "Backend did not respond. Refresh to retry."); }
        catch (FileNotFoundException) { throw new BackendFailure(ConnectionState.Disconnected, "Backend connection metadata is not available. Start the backend, then Refresh."); }
        catch (DirectoryNotFoundException) { throw new BackendFailure(ConnectionState.Disconnected, "Backend has not been initialized. Start it, then Refresh."); }
        catch (UnauthorizedAccessException) { throw new BackendFailure(ConnectionState.AuthenticationFailed, "Cannot read protected local connection metadata."); }
        catch (IOException) { throw new BackendFailure(ConnectionState.Unavailable, "Local connection metadata is unavailable or has unsafe permissions."); }
        catch (JsonException) { throw new BackendFailure(ConnectionState.Unavailable, "Backend response or connection metadata is invalid."); }
        catch (InvalidOperationException) { throw new BackendFailure(ConnectionState.Unavailable, "Local connection configuration is invalid."); }
    }
}
