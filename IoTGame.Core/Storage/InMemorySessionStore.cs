using System.Collections.Concurrent;
using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;

namespace IoTGame.Core.Storage;

public class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();

    public Task SaveAsync(GameSession session)
    {
        _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    public Task<GameSession?> GetAsync(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task DeleteAsync(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<GameSession>> GetAllActiveAsync() =>
        Task.FromResult<IEnumerable<GameSession>>(
            _sessions.Values.Where(s => s.Status == SessionStatus.Active).ToList());
}
