using Arbitrage.Application;
using Arbitrage.Contracts;

namespace Arbitrage.Backend;

public static class RealtimeOrderBookEndpoints
{
    public static void MapRealtimeOrderBooks(this RouteGroupBuilder api)
    {
        foreach (var start in new[] { true, false })
            api.MapPost("/workspaces/{workspaceId:guid}/orderbooks/{exchange}/{marketId}/realtime/" + (start ? "start" : "stop"),
                async (Guid workspaceId, string exchange, string marketId, string? instrumentId, WorkspaceService workspaces,
                    OrderBookService books, RealtimeOrderBookManager manager, CancellationToken ct) =>
                {
                    if (!(await workspaces.ReadAsync(workspaceId, ct)).IsSuccess) return Results.StatusCode(403);
                    try
                    {
                        var resolved = await books.ResolveAsync(exchange, marketId, instrumentId, ct);
                        if (resolved?.Request is not { } request) return Results.NotFound();
                        if (start) manager.Start(workspaceId, request); else manager.Stop(workspaceId, request.Instrument);
                        return Results.Ok(await books.ReadAsync(exchange, marketId, instrumentId, false, ct));
                    }
                    catch (ArgumentException) { return Results.BadRequest(); }
                    catch (InvalidOperationException e) when (e.Message == "RealtimeCapacity") { return Results.Conflict(); }
                });
        var credentials = api.MapGroup("/local-runtime/kalshi-credentials");
        credentials.MapGet("", async (IRequestActor actor, ILocalProfileStore profiles, IExchangeCredentialStore store, CancellationToken ct) =>
            actor.UserId != (await profiles.GetAsync(ct)).UserId ? Results.StatusCode(403) : Results.Ok(store.Status()));
        credentials.MapPost("/import", async (ImportCredentialRequest request, IRequestActor actor, ILocalProfileStore profiles,
            IExchangeCredentialStore store, RealtimeOrderBookManager manager, CancellationToken ct) =>
        {
            if (actor.UserId != (await profiles.GetAsync(ct)).UserId) return Results.StatusCode(403);
            try
            {
                var result = store.Import(request.KeyId, request.FilePath, request.ConfirmReplacement, request.ExpectedVersion);
                manager.CredentialsChanged(); return Results.Ok(result);
            }
            catch (CredentialOperationException e) { return Results.BadRequest(new { Code = e.Message }); }
        });
        credentials.MapPost("/remove", async (RemoveCredentialRequest request, IRequestActor actor, ILocalProfileStore profiles,
            IExchangeCredentialStore store, RealtimeOrderBookManager manager, CancellationToken ct) =>
        {
            if (actor.UserId != (await profiles.GetAsync(ct)).UserId) return Results.StatusCode(403);
            try
            {
                var result = store.Remove(request.Confirmed, request.ExpectedVersion);
                manager.CredentialsChanged(); return Results.Ok(result);
            }
            catch (CredentialOperationException e) { return Results.BadRequest(new { Code = e.Message }); }
        });
    }
}
