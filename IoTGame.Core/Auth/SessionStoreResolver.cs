using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;

namespace IoTGame.Core.Auth;

public class SessionStoreResolver : ISessionResolver
{
    private readonly ISessionStore _store;

    public SessionStoreResolver(ISessionStore store) => _store = store;

    public async Task<SessionContext?> ResolveAsync(string deviceId)
    {
        var sessions = await _store.GetAllActiveAsync();
        var session = sessions.FirstOrDefault(s =>
            s.Devices.Any(d => d.DeviceId == deviceId));
        if (session is null) return null;
        return new SessionContext(session.SessionId, session.UnityEndpoint);
    }
}
