using IoTGame.Core.Models;

namespace IoTGame.Core.Abstractions;

public interface IPlayerMapper
{
    Task<PlayerMapping?> GetPlayerMappingAsync(string deviceId, string slotId, string sessionId);
}
