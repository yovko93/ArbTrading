using Arbitrage.Application;
using Arbitrage.Contracts;

namespace Arbitrage.Backend;

public static class OrderBookEndpoints
{
    public static void MapOrderBooks(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/orderbooks/{exchange}/{marketId}");
        group.MapGet("", (Guid workspaceId, string exchange, string marketId, string? instrumentId,
            WorkspaceService workspaces, OrderBookService books, CancellationToken ct) =>
            Read(workspaceId, exchange, marketId, instrumentId, false, workspaces, books, ct));
        group.MapPost("/refresh", (Guid workspaceId, string exchange, string marketId, string? instrumentId,
            WorkspaceService workspaces, OrderBookService books, CancellationToken ct) =>
            Read(workspaceId, exchange, marketId, instrumentId, true, workspaces, books, ct));
        group.MapPost("/depth", async (Guid workspaceId, string exchange, string marketId, DepthPreviewRequest request,
            WorkspaceService workspaces, OrderBookService books, CancellationToken ct) =>
        {
            if (!(await workspaces.ReadAsync(workspaceId, ct)).IsSuccess) return Results.StatusCode(403);
            try
            {
                var result = await books.PreviewAsync(exchange, marketId, request, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (OverflowException) { return Results.BadRequest(); }
        });
    }
    private static async Task<IResult> Read(Guid workspaceId, string exchange, string marketId, string? instrumentId,
        bool refresh, WorkspaceService workspaces, OrderBookService books, CancellationToken ct)
    {
        if (!(await workspaces.ReadAsync(workspaceId, ct)).IsSuccess) return Results.StatusCode(403);
        try
        {
            var result = await books.ReadAsync(exchange, marketId, instrumentId, refresh, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (InvalidOperationException e) when (e.Message == "RefreshBusy") { return Results.Conflict(); }
    }
}
