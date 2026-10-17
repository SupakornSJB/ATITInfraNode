using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;
using IoTGame.Core.Services;

namespace IoTGame.Core.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").AddEndpointFilter<AdminAuthFilter>();

        group.MapPost("/start", async (UnityRelayService relay, ISessionStore store, IoTGameOptions options, GameStateService gameState) =>
        {
            await SendToAllEndpoints(relay, store, options, "start_round");
            gameState.MarkStarted();
            return Results.Ok(new { round = "active", startedAt = gameState.StartedAt });
        });

        group.MapPost("/end", async (UnityRelayService relay, ISessionStore store, IoTGameOptions options, GameStateService gameState) =>
        {
            await SendToAllEndpoints(relay, store, options, "end_round");
            gameState.MarkEnded();
            return Results.Ok(new { round = "ended", endedAt = gameState.EndedAt });
        });

        group.MapPost("/reset", async (UnityRelayService relay, ISessionStore store, IoTGameOptions options, GameStateService gameState) =>
        {
            await SendToAllEndpoints(relay, store, options, "reset");
            gameState.Reset();
            return Results.Ok(new { round = "idle" });
        });
    }

    private static async Task SendToAllEndpoints(
        UnityRelayService relay, ISessionStore store, IoTGameOptions options, string command)
    {
        var sessions = (await store.GetAllActiveAsync()).ToList();
        if (sessions.Count > 0)
        {
            foreach (var session in sessions)
                await relay.SendAdminCommand(session.UnityEndpoint, command, "admin");
        }
        else if (!string.IsNullOrEmpty(options.DefaultUnityEndpoint))
        {
            await relay.SendAdminCommand(options.DefaultUnityEndpoint, command, "admin");
        }
    }
}

internal class AdminAuthFilter : IEndpointFilter
{
    private readonly IAdminAuthenticator _auth;

    public AdminAuthFilter(IAdminAuthenticator auth) => _auth = auth;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await _auth.IsAuthorizedAsync(context.HttpContext, context.HttpContext.Request.Path))
            return Results.Unauthorized();
        return await next(context);
    }
}
