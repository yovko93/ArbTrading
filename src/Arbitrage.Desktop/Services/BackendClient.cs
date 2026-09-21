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
    public string? LastEndpoint { get; private set; }
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
            // Re-read on every operation; one safe retry after a 401 handles credential rotation.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var connection = await connections.ReadAsync(cancellationToken);
                using var request = new HttpRequestMessage(method, new Uri(new Uri(connection.BaseUrl + "/"), path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Credential);
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
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
        catch (HttpRequestException) { throw new BackendFailure(ConnectionState.Disconnected, "Backend disconnected. Start it independently, then Refresh."); }
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
