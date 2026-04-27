using IoTGame.Core.Auth;
using IoTGame.Core.Models;
using IoTGame.Core.Services;

namespace IoTGame.Core.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sessions").AddEndpointFilter<AdminAuthFilter>();

        group.MapPost("", async (CreateSessionRequest req, SessionManager mgr) =>
        {
            var session = await mgr.CreateAsync(req.UnityEndpoint, req.Devices);
            return Results.Created($"/api/sessions/{session.SessionId}", session);
        });

        group.MapGet("", async (SessionManager mgr) =>
            Results.Ok(await mgr.GetAllActiveAsync()));

        group.MapGet("{sessionId}", async (string sessionId, SessionManager mgr) =>
        {
            var session = await mgr.GetAsync(sessionId);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        group.MapPost("{sessionId}/start", async (string sessionId, StartSessionRequest? req, SessionManager mgr) =>
        {
            var ok = await mgr.StartAsync(sessionId, req?.SessionParams);
            return ok ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("{sessionId}/end", async (string sessionId, SessionManager mgr) =>
        {
            var ok = await mgr.EndAsync(sessionId);
            return ok ? Results.Ok() : Results.NotFound();
        });

        group.MapDelete("{sessionId}", async (string sessionId, SessionManager mgr) =>
        {
            var ok = await mgr.DeleteAsync(sessionId);
            return ok ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("{sessionId}/devices", async (string sessionId, SessionDeviceConfig device, SessionManager mgr) =>
        {
            var ok = await mgr.AddDeviceAsync(sessionId, device);
            return ok ? Results.Ok() : Results.NotFound();
        });

        group.MapDelete("{sessionId}/devices/{deviceId}", async (string sessionId, string deviceId, SessionManager mgr) =>
        {
            var ok = await mgr.RemoveDeviceAsync(sessionId, deviceId);
            return ok ? Results.Ok() : Results.NotFound();
        });

        // Token flow (only registered when WithSessionTokenAuth() is called)
        group.MapPost("{sessionId}/join", async (string sessionId, SessionTokenService tokenSvc) =>
        {
            var result = await tokenSvc.IssueAsync(sessionId);
            return result is null
                ? Results.NotFound()
                : Results.Ok(new { token = result.Value.Token, expiresIn = result.Value.ExpiresIn });
        });

        group.MapGet("{sessionId}/validate", async (string sessionId, string token, SessionTokenService tokenSvc) =>
        {
            var valid = await tokenSvc.ValidateAsync(sessionId, token);
            return Results.Ok(new { valid });
        });
    }
}

public record CreateSessionRequest(string UnityEndpoint, List<SessionDeviceConfig> Devices);
public record StartSessionRequest(Dictionary<string, float>? SessionParams);
