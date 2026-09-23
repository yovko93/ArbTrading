using System.Net.Http;
using Arbitrage.Contracts;
namespace Arbitrage.Desktop.Services;
public sealed partial class BackendClient
{
    public Task<PaperResolutionCandidateResponse[]> ResolutionCandidatesAsync(Guid w, Guid g, int page, CancellationToken ct) => SendAsync<PaperResolutionCandidateResponse[]>(HttpMethod.Get, $"api/v1/workspaces/{w}/paper/resolution-candidates?generationId={g}&page={page}", null, ct);
    public Task<PaperResolutionPreviewResponse> ResolutionPreviewAsync(Guid w, PaperResolutionSelection r, CancellationToken ct) => SendAsync<PaperResolutionPreviewResponse>(HttpMethod.Post, $"api/v1/workspaces/{w}/paper/resolutions/preview", r, ct);
    public Task<PaperResolutionCommitResponse> ConfirmResolutionAsync(Guid w, ConfirmResolutionRequest r, CancellationToken ct) => SendAsync<PaperResolutionCommitResponse>(HttpMethod.Post, $"api/v1/workspaces/{w}/paper/resolutions/confirm", r, ct);
    public Task<PaperResolutionResponse[]> ResolutionHistoryAsync(Guid w, Guid g, int page, CancellationToken ct) => SendAsync<PaperResolutionResponse[]>(HttpMethod.Get, $"api/v1/workspaces/{w}/paper/resolutions?generationId={g}&page={page}", null, ct);
    public Task<PaperResolutionResponse> ResolutionDetailAsync(Guid w, Guid id, CancellationToken ct) => SendAsync<PaperResolutionResponse>(HttpMethod.Get, $"api/v1/workspaces/{w}/paper/resolutions/{id}", null, ct);
    public Task<PaperPerformanceResponse> PaperPerformanceAsync(Guid w, Guid g, CancellationToken ct) => SendAsync<PaperPerformanceResponse>(HttpMethod.Get, $"api/v1/workspaces/{w}/paper/performance?generationId={g}", null, ct);
    public Task<PaperCurveResponse> PaperCurveAsync(Guid w, Guid g, string exchange, string currency, int page, CancellationToken ct) => SendAsync<PaperCurveResponse>(HttpMethod.Get,
        $"api/v1/workspaces/{w}/paper/performance/curve?generationId={g}&exchange={Uri.EscapeDataString(exchange)}&currency={Uri.EscapeDataString(currency)}&page={page}", null, ct);
    public Task<PaperPositionResponse[]> PaperPositionHistoryAsync(Guid w, Guid g, string status, int page, CancellationToken ct) => SendAsync<PaperPositionResponse[]>(HttpMethod.Get,
        $"api/v1/workspaces/{w}/paper/positions?generationId={g}&status={status}&page={page}", null, ct);
}
