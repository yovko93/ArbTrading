using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json.Serialization;
using Arbitrage.Application;
using Arbitrage.Backend;
using Arbitrage.Contracts;
using Arbitrage.Infrastructure;
using Arbitrage.LocalTransport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Serilog;
using Serilog.Events;

public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { await RunAsync(args); return 0; }
        catch (Exception exception)
        {
            // Do not print raw configuration, paths containing secrets, or exception messages.
            Console.Error.WriteLine("Backend failed ({0}). Check local-only configuration, storage permissions, exclusive ownership, and migration requirements. The backend is stopped.", exception.GetType().Name);
            return 1;
        }
    }

    private static async Task RunAsync(string[] args)
    {
        var migrateOnly = args.Contains("--migrate", StringComparer.Ordinal);
        var builder = WebApplication.CreateBuilder(args.Where(a => a != "--migrate").ToArray());
        var settings = builder.Configuration.GetSection("Local").Get<LocalOptions>() ?? new();
        var endpoint = settings.Validate(builder.Configuration);
        using var lease = new LocalRuntimeLease(settings.DataDirectory, settings.RuntimeDirectory);
        var databasePath = Path.Combine(settings.DataDirectory, "arbitrage.db");
        ProtectedStorage.RejectLinks(databasePath);
        var existing = File.Exists(databasePath);
        var dbOptions = DatabaseOptions.ForFile(databasePath);
        using var logger = new LoggerConfiguration().MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext().WriteTo.Console()
            .WriteTo.File(Path.Combine(settings.DataDirectory, "logs", "backend-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7, fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true).CreateLogger();
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(logger);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Parse(endpoint.Host.Trim('[', ']')), endpoint.Port));
        builder.Services.Configure<LocalOptions>(builder.Configuration.GetSection("Local"));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<BackendInstance>();
        builder.Services.AddSingleton<BackendDiagnosticStore>();
        builder.Services.AddSingleton<RealtimePublisher>();
        builder.Services.AddSingleton<LocalShutdownCoordinator>();
        builder.Services.AddHostedService<RealtimeDispatchService>();
        builder.Services.AddHostedService<ApplicationHeartbeatService>();
        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 4096;
            options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(35);
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(dbOptions);
        builder.Services.AddScoped<TradingDbContext>();
        builder.Services.AddScoped<DatabaseInitializer>();
        builder.Services.AddScoped<LocalStore>();
        builder.Services.AddScoped<ILocalProfileStore>(s => s.GetRequiredService<LocalStore>());
        builder.Services.AddScoped<IWorkspaceStore>(s => s.GetRequiredService<LocalStore>());
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IRequestActor, RequestActor>();
        builder.Services.AddScoped<WorkspaceService>();
        var credential = new LocalCredential();
        builder.Services.AddSingleton(credential);
        builder.Services.AddAuthentication(LocalAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LocalAuthenticationHandler>(LocalAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);

        await using var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(existing, migrateOnly, CancellationToken.None);
        if (migrateOnly) { logger.Information("Database migrations and local ownership verified"); return; }
        var started = Stopwatch.StartNew();
        app.Use(async (context, next) =>
        {
            // Generate correlation server-side: untrusted headers cannot forge the audit actor or correlation.
            context.TraceIdentifier = Guid.NewGuid().ToString("N");
            context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
            context.Response.Headers.CacheControl = "no-store";
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.Error("Request failed: {ErrorType}; correlation {CorrelationId}", exception.GetType().Name, context.TraceIdentifier);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 503;
                    await context.Response.WriteAsJsonAsync(new ApiError("Unavailable", "Local service is unavailable.", context.TraceIdentifier), context.RequestAborted);
                }
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health/live", () => Results.Ok(new { status = "Live" })).AllowAnonymous();
        app.MapHub<ApplicationHub>("/hubs/v1/application").RequireAuthorization();
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/system/status", async (ILocalProfileStore profiles, CancellationToken ct) => new SystemStatusResponse(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown", started.Elapsed.TotalSeconds,
            await profiles.IsHealthyAsync(ct) ? "Healthy" : "Unavailable", "Local", settings.TradingMode, "Paper", Capabilities.Phase01A));
        api.MapGet("/session", async (ILocalProfileStore profiles, WorkspaceService workspaces, CancellationToken ct) =>
        {
            var profile = await profiles.GetAsync(ct);
            var workspace = await workspaces.ReadAsync(profile.DefaultWorkspaceId, ct);
            return workspace.IsSuccess ? Results.Ok(new SessionResponse(profile.UserId, profile.DefaultWorkspaceId, "Local", Capabilities.Phase01A)) : Results.StatusCode(503);
        });
        api.MapGet("/exchanges/status", () => new[] { new ExchangeStatusResponse("Polymarket", "NotImplemented"), new ExchangeStatusResponse("Kalshi", "NotImplemented") });
        api.MapGet("/trading/mode", () => new TradingModeResponse(settings.TradingMode, "Paper", Capabilities.Phase01A));
        api.MapGet("/workspaces/{workspaceId:guid}/snapshot", async (Guid workspaceId, WorkspaceService workspaces,
            ILocalProfileStore profiles, BackendInstance instance, HttpContext context, CancellationToken ct) =>
        {
            var workspace = await workspaces.ReadAsync(workspaceId, ct);
            if (!workspace.IsSuccess) return Map(workspace, context);
            var profile = await profiles.GetAsync(ct);
            var status = new SystemStatusResponse(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                started.Elapsed.TotalSeconds, await profiles.IsHealthyAsync(ct) ? "Healthy" : "Unavailable",
                "Local", settings.TradingMode, "Paper", Capabilities.Phase01A);
            return Results.Ok(new ApplicationSnapshotResponse(1, instance.Id, profile.Id, DateTimeOffset.UtcNow,
                new SessionResponse(profile.UserId, workspaceId, "Local", Capabilities.Phase01A), status,
                new WorkspaceSettingsResponse(workspaceId, workspace.Value!.DisplayName),
                [new("Polymarket", "NotImplemented"), new("Kalshi", "NotImplemented")]));
        });
        api.MapGet("/workspaces/{workspaceId:guid}/diagnostics", async (Guid workspaceId, long? after, int? take,
            WorkspaceService workspaces, BackendDiagnosticStore diagnostics, HttpContext context, CancellationToken ct) =>
        {
            var workspace = await workspaces.ReadAsync(workspaceId, ct);
            if (!workspace.IsSuccess) return Map(workspace, context);
            if (after is < 0 || take is < 1 or > 100) return Results.BadRequest();
            return Results.Ok(diagnostics.Recent(workspaceId, after ?? 0, take ?? 100));
        });
        api.MapGet("/workspaces/{workspaceId:guid}/settings", async (Guid workspaceId, WorkspaceService service, HttpContext context, CancellationToken ct) =>
            Map(await service.ReadAsync(workspaceId, ct), context));
        api.MapPut("/workspaces/{workspaceId:guid}/settings", async (Guid workspaceId, UpdateWorkspaceSettingsRequest request,
            WorkspaceService service, RealtimePublisher realtime, BackendInstance instance, HttpContext context, CancellationToken ct) =>
        {
            var result = await service.RenameAsync(workspaceId, request.DisplayName, context.TraceIdentifier, ct);
            if (result.IsSuccess)
            {
                try { realtime.WorkspaceChanged(instance.Id, workspaceId, context.TraceIdentifier); }
                catch (Exception exception) { logger.Warning("Committed workspace change notification failed: {ErrorType}", exception.GetType().Name); }
            }
            return Map(result, context);
        });
        api.MapPost("/local-runtime/stop", async (StopLocalRuntimeRequest request, IRequestActor actor,
            ILocalProfileStore profiles, BackendInstance instance, LocalShutdownCoordinator shutdown,
            RealtimePublisher realtime, HttpContext context, CancellationToken ct) =>
        {
            if (!settings.ManagedLocal) return Results.StatusCode(403);
            var profile = await profiles.GetAsync(ct);
            if (actor.UserId != profile.UserId) return Results.StatusCode(403);
            if (request.ExpectedBackendInstanceId != instance.Id) return Results.Conflict();
            try
            {
                realtime.Diagnostic(profile.DefaultWorkspaceId, "Warning", "Runtime", "StopRequested",
                    "An authorized local desktop requested backend shutdown.", context.TraceIdentifier);
            }
            catch (Exception exception)
            { logger.Warning("Stop notification failed: {ErrorType}", exception.GetType().Name); }
            return Results.Ok(shutdown.Accept(context));
        });

        // Publish credentials only after persistence and the listening socket are ready.
        await app.StartAsync();
        try
        {
            ILocalConnectionFile connectionFile = new ProtectedLocalConnectionFile(settings.RuntimeDirectory);
            await connectionFile.WriteAsync(new(endpoint.GetLeftPart(UriPartial.Authority), credential.Value), CancellationToken.None);
            await using (var readyScope = app.Services.CreateAsyncScope())
            {
                var profile = await readyScope.ServiceProvider.GetRequiredService<ILocalProfileStore>().GetAsync(CancellationToken.None);
                app.Services.GetRequiredService<RealtimePublisher>().Diagnostic(profile.DefaultWorkspaceId,
                    "Information", "Backend", "Ready", "Local backend is ready; execution is unavailable.");
            }
            logger.Information("Local backend initialized; execution unavailable");
            await app.WaitForShutdownAsync();
        }
        finally { await app.StopAsync(); }
    }

    private static IResult Map(Result<Arbitrage.Domain.WorkspaceSettings> result, HttpContext context) => result.IsSuccess
        ? Results.Ok(new WorkspaceSettingsResponse(result.Value!.WorkspaceId, result.Value.DisplayName))
        : Results.Json(new ApiError(result.Failure.ToString(), result.Error!, context.TraceIdentifier), statusCode: result.Failure switch
        { Failure.Unauthenticated => 401, Failure.NotFound => 404, Failure.InvalidInput => 400, _ => 503 });
}
