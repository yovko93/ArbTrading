using System.Net.Http;
using Arbitrage.Contracts;
namespace Arbitrage.Desktop.Services;
public sealed partial class BackendClient
{
    public Task<PaperValuationResponse> PaperValuationAsync(Guid workspace, Guid generation, int page, CancellationToken ct) =>
        SendAsync<PaperValuationResponse>(HttpMethod.Get, $"api/v1/workspaces/{workspace}/paper/valuation?generationId={generation}&page={page}", null, ct);
}
