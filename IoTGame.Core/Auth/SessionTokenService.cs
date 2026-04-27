using System.Collections.Concurrent;
using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;

namespace IoTGame.Core.Auth;

public class SessionTokenService
{
    private record TokenEntry(string SessionId, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new();
    private readonly ISessionStore _store;

    public SessionTokenService(ISessionStore store) => _store = store;

    public async Task<(string Token, int ExpiresIn)?> IssueAsync(string sessionId, int ttlSeconds = 60)
    {
        var session = await _store.GetAsync(sessionId);
        if (session is null || session.Status != SessionStatus.Active) return null;

        var token = $"tok_{Guid.NewGuid():N}"[..16];
        _tokens[token] = new TokenEntry(sessionId, DateTime.UtcNow.AddSeconds(ttlSeconds));
        return (token, ttlSeconds);
    }

    public Task<bool> ValidateAsync(string sessionId, string token)
    {
        if (!_tokens.TryGetValue(token, out var entry)) return Task.FromResult(false);
        if (entry.SessionId != sessionId || entry.ExpiresAt < DateTime.UtcNow)
        {
            _tokens.TryRemove(token, out _);
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }
}
