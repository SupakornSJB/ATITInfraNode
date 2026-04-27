using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;

namespace GameServer.Mapping;

// Reads device/slot mappings from the DeviceRegistry section in appsettings.json.
// sessionId is ignored — all events route through the same static config.
public class StaticDeviceRegistryMapper : IPlayerMapper
{
    private readonly Dictionary<string, DeviceEntry> _devices;

    public StaticDeviceRegistryMapper(IConfiguration configuration)
    {
        _devices = configuration
            .GetSection("DeviceRegistry:devices")
            .Get<Dictionary<string, DeviceEntry>>() ?? new();
    }

    public Task<PlayerMapping?> GetPlayerMappingAsync(string deviceId, string slotId, string sessionId)
    {
        if (!_devices.TryGetValue(deviceId, out var device))
            return Task.FromResult<PlayerMapping?>(null);

        if (!device.Inputs.TryGetValue(slotId, out var input))
            return Task.FromResult<PlayerMapping?>(null);

        var playerId = input.PlayerId ?? device.DefaultPlayerId;
        if (playerId is null && input.InputRole == "player")
            return Task.FromResult<PlayerMapping?>(null);

        return Task.FromResult<PlayerMapping?>(new PlayerMapping(
            PlayerId: playerId,
            InputName: input.InputName,
            InputRole: input.InputRole,
            EnvKey: input.EnvKey,
            MinRaw: input.MinRaw,
            MaxRaw: input.MaxRaw,
            AuxiliaryKeys: null
        ));
    }
}

// Config-binding models matching the old DeviceRegistry appsettings schema
class DeviceEntry
{
    public string? DefaultPlayerId { get; init; }
    public Dictionary<string, InputEntry> Inputs { get; init; } = new();
}

class InputEntry
{
    public string  Type       { get; init; } = "";
    public string  InputName  { get; init; } = "";
    public string? PlayerId   { get; init; }
    public float?  MinRaw     { get; init; }
    public float?  MaxRaw     { get; init; }
    public string  InputRole  { get; init; } = "player";
    public string? EnvKey     { get; init; }
}
