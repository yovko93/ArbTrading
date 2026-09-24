using System.Net.Http;
using Arbitrage.Contracts;
namespace Arbitrage.Desktop.Services;
public sealed partial class BackendClient
{
    public Task<PaperSizingPreviewResponse> PaperSizingPreviewAsync(Guid workspace, PaperSizingPreviewRequest request, CancellationToken ct) =>
        SendAsync<PaperSizingPreviewResponse>(HttpMethod.Post, $"api/v1/workspaces/{workspace}/paper/automation/sizing-preview", request, ct);
    public Task<PaperAutomationStatusResponse> PaperAutomationStatusAsync(Guid workspace, CancellationToken ct) =>
        SendAsync<PaperAutomationStatusResponse>(HttpMethod.Get, $"api/v1/workspaces/{workspace}/paper/automation/status", null, ct);
    public Task<PaperAutomationProfileResponse> SavePaperAutomationAsync(Guid workspace, SavePaperAutomationRequest request, CancellationToken ct) =>
        SendAsync<PaperAutomationProfileResponse>(HttpMethod.Put, $"api/v1/workspaces/{workspace}/paper/automation/profile", request, ct);
    public Task<PaperAutomationStatusResponse> ControlPaperAutomationAsync(Guid workspace, string action, object? request, CancellationToken ct) =>
        SendAsync<PaperAutomationStatusResponse>(HttpMethod.Post, $"api/v1/workspaces/{workspace}/paper/automation/{action}", request, ct);
}
