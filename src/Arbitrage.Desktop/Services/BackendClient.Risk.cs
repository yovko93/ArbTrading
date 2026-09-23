using System.Net.Http;
using Arbitrage.Contracts;
namespace Arbitrage.Desktop.Services;
public sealed partial class BackendClient
{
    public Task<PaperRiskStatusResponse> PaperRiskStatusAsync(Guid workspace, Guid? generation, CancellationToken ct) =>
        SendAsync<PaperRiskStatusResponse>(HttpMethod.Get, $"api/v1/workspaces/{workspace}/paper/admission-status" + (generation.HasValue ? $"?generationId={generation}" : ""), null, ct);
    public Task<PaperRiskPolicyResponse> SavePaperRiskPolicyAsync(Guid workspace, SavePaperRiskPolicyRequest request, CancellationToken ct) =>
        SendAsync<PaperRiskPolicyResponse>(HttpMethod.Put, $"api/v1/workspaces/{workspace}/paper/admission-policy", request, ct);
}
