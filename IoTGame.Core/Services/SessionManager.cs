using GameController.V2;
using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;
using Microsoft.Extensions.Logging;

namespace IoTGame.Core.Services;

public class SessionManager
{
    private readonly ISessionStore _store;
    private readonly UnityRelayService _relay;
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(ISessionStore store, UnityRelayService relay, ILogger<SessionManager> logger)
    {
        _store = store;
        _relay = relay;
        _logger = logger;
    }

    public async Task<GameSession> CreateAsync(string unityEndpoint, List<SessionDeviceConfig> devices)
    {
        var session = new GameSession
        {
            UnityEndpoint = unityEndpoint,
            Devices       = devices
        };
        await _store.SaveAsync(session);
        _logger.LogInformation("Session {SessionId} created for {Endpoint}", session.SessionId, unityEndpoint);
        return session;
    }

    public async Task<bool> StartAsync(string sessionId, Dictionary<string, float>? sessionParams = null)
    {
        var session = await _store.GetAsync(sessionId);
        if (session is null)
        {
            _logger.LogWarning("StartAsync: session {SessionId} not found", sessionId);
            return false;
        }

        session.Status    = SessionStatus.Active;
        session.StartedAt = DateTime.UtcNow;
        await _store.SaveAsync(session);

        var request = new AdminCommandRequest
        {
            Command   = "session_start",
            IssuedBy  = "system",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        if (sessionParams is not null)
            foreach (var kv in sessionParams) request.Auxiliary[kv.Key] = kv.Value;
        if (session.Devices.Count > 0)
            request.Auxiliary["player_count"] = session.Devices.SelectMany(d => d.Slots).Select(s => s.PlayerId ?? "").Distinct().Count(p => !string.IsNullOrEmpty(p));

        await _relay.SendAdminCommand(session.UnityEndpoint, request);
        _logger.LogInformation("Session {SessionId} started", sessionId);
        return true;
    }

    public async Task<bool> EndAsync(string sessionId)
    {
        var session = await _store.GetAsync(sessionId);
        if (session is null)
        {
            _logger.LogWarning("EndAsync: session {SessionId} not found", sessionId);
            return false;
        }

        session.Status  = SessionStatus.Ended;
        session.EndedAt = DateTime.UtcNow;
        await _store.SaveAsync(session);

        await _relay.SendAdminCommand(session.UnityEndpoint, "end_round", "system");
        _logger.LogInformation("Session {SessionId} ended", sessionId);
        return true;
    }

    public async Task<bool> DeleteAsync(string sessionId)
    {
        var session = await _store.GetAsync(sessionId);
        if (session is null) return false;
        await _store.DeleteAsync(sessionId);
        return true;
    }

    public Task<GameSession?> GetAsync(string sessionId) => _store.GetAsync(sessionId);

    public Task<IEnumerable<GameSession>> GetAllActiveAsync() => _store.GetAllActiveAsync();

    public async Task<bool> AddDeviceAsync(string sessionId, SessionDeviceConfig device)
    {
        var session = await _store.GetAsync(sessionId);
        if (session is null) return false;

        var existing = session.Devices.FindIndex(d => d.DeviceId == device.DeviceId);
        if (existing >= 0)
            session.Devices[existing] = device;
        else
            session.Devices.Add(device);

        await _store.SaveAsync(session);
        return true;
    }

    public async Task<bool> RemoveDeviceAsync(string sessionId, string deviceId)
    {
        var session = await _store.GetAsync(sessionId);
        if (session is null) return false;

        var removed = session.Devices.RemoveAll(d => d.DeviceId == deviceId) > 0;
        if (removed) await _store.SaveAsync(session);
        return removed;
    }
}
