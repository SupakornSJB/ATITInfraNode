using IoTGame.Core.Abstractions;
using IoTGame.Core.Services;

namespace IoTGame.Core.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").AddEndpointFilter<AdminAuthFilter>();

        group.MapPost("/start", async (UnityRelayService relay, ISessionStore store) =>
        {
            foreach (var session in await store.GetAllActiveAsync())
                await relay.SendAdminCommand(session.UnityEndpoint, "start_round", "admin");
            return Results.Ok();
        });

        group.MapPost("/end", async (UnityRelayService relay, ISessionStore store) =>
        {
            foreach (var session in await store.GetAllActiveAsync())
                await relay.SendAdminCommand(session.UnityEndpoint, "end_round", "admin");
            return Results.Ok();
        });

        group.MapPost("/reset", async (UnityRelayService relay, ISessionStore store) =>
        {
            foreach (var session in await store.GetAllActiveAsync())
                await relay.SendAdminCommand(session.UnityEndpoint, "reset", "admin");
            return Results.Ok();
        });
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
