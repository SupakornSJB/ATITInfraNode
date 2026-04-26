using ATITInfraNode.Services;

namespace ATITInfraNode.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/start", async (UnityRelayService relay) =>
        {
            await relay.SendAdminCommand("start_round", "admin");
            return Results.Ok();
        });

        app.MapPost("/api/admin/end", async (UnityRelayService relay) =>
        {
            await relay.SendAdminCommand("end_round", "admin");
            return Results.Ok();
        });

        app.MapPost("/api/admin/reset", async (UnityRelayService relay) =>
        {
            await relay.SendAdminCommand("reset", "admin");
            return Results.Ok();
        });
    }
}