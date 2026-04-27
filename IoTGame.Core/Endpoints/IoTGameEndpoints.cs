namespace IoTGame.Core.Endpoints;

public static class IoTGameEndpoints
{
    public static void MapIoTGameEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok("healthy"));
        app.MapAdminEndpoints();
        app.MapSessionEndpoints();
    }
}
