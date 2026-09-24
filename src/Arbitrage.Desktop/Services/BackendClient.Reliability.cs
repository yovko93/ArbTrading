using System.Net.Http;
using Arbitrage.Contracts;
namespace Arbitrage.Desktop.Services;
public sealed partial class BackendClient
{
    public Task<PaperReliabilityCurrentResponse> PaperReliabilityCurrentAsync(Guid workspace, CancellationToken ct) => SendAsync<PaperReliabilityCurrentResponse>(HttpMethod.Get, $"api/v1/workspaces/{workspace}/paper/reliability/current", null, ct);
    public Task<PaperReliabilityCampaignResponse[]> PaperReliabilityHistoryAsync(Guid workspace, int page, CancellationToken ct) => SendAsync<PaperReliabilityCampaignResponse[]>(HttpMethod.Get, $"api/v1/workspaces/{workspace}/paper/reliability/campaigns?page={page}", null, ct);
    public Task<PaperReliabilityReportResponse?> PaperReliabilityReportAsync(Guid workspace, Guid campaign, CancellationToken ct) => SendAsync<PaperReliabilityReportResponse?>(HttpMethod.Get, $"api/v1/workspaces/{workspace}/paper/reliability/campaigns/{campaign}/report", null, ct);
    public Task<PaperReliabilityCampaignResponse> PaperReliabilityActionAsync(Guid workspace, string action, PaperReliabilityActionRequest request, CancellationToken ct) => SendAsync<PaperReliabilityCampaignResponse>(HttpMethod.Post, $"api/v1/workspaces/{workspace}/paper/reliability/{action}", request, ct);
    public Task<PaperReliabilityExportResponse> PaperReliabilityExportAsync(Guid workspace, Guid campaign, CancellationToken ct) => SendAsync<PaperReliabilityExportResponse>(HttpMethod.Post, $"api/v1/workspaces/{workspace}/paper/reliability/campaigns/{campaign}/export", null, ct);
}
