using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;

namespace IoTGame.Core.Auth;

public class SessionStorePlayerMapper : IPlayerMapper
{
    private readonly ISessionStore _store;

    public SessionStorePlayerMapper(ISessionStore store) => _store = store;

    public async Task<PlayerMapping?> GetPlayerMappingAsync(string deviceId, string slotId, string sessionId)
    {
        var session = await _store.GetAsync(sessionId);
        if (session is null) return null;

        var device = session.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (device is null) return null;

        var slot = device.Slots.FirstOrDefault(s => s.SlotId == slotId);
        if (slot is null) return null;

        var playerId = slot.PlayerId ?? device.DefaultPlayerId;
        if (playerId is null && slot.InputRole == "player") return null;

        return new PlayerMapping(
            PlayerId: playerId,
            InputName: slot.InputName,
            InputRole: slot.InputRole,
            EnvKey: slot.EnvKey,
            MinRaw: slot.MinRaw,
            MaxRaw: slot.MaxRaw,
            AuxiliaryKeys: slot.AuxiliaryKeys
        );
    }
}
