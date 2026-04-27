using IoTGame.Core.Models;

namespace IoTGame.Core.Abstractions;

public interface ISessionStore
{
    Task SaveAsync(GameSession session);
    Task<GameSession?> GetAsync(string sessionId);
    Task DeleteAsync(string sessionId);
    Task<IEnumerable<GameSession>> GetAllActiveAsync();
}
