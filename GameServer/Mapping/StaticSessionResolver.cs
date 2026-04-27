using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;

namespace GameServer.Mapping;

// Always routes to the configured Unity endpoint — no session management needed.
// All devices map to the single "default" session.
public class StaticSessionResolver : ISessionResolver
{
    private readonly string _endpoint;

    public StaticSessionResolver(IConfiguration configuration)
    {
        _endpoint = configuration["Unity:GrpcEndpoint"]
            ?? throw new InvalidOperationException("Unity:GrpcEndpoint is not configured");
    }

    public Task<SessionContext?> ResolveAsync(string deviceId) =>
        Task.FromResult<SessionContext?>(new SessionContext("default", _endpoint));
}
