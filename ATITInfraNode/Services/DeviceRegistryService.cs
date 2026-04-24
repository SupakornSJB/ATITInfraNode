using ATITInfraNode.Models;

namespace ATITInfraNode.Services;

public class DeviceRegistryService
{
    private readonly DeviceRegistryConfig _config;

    public DeviceRegistryService(IConfiguration configuration)
    {
        _config = configuration.GetSection("DeviceRegistry").Get<DeviceRegistryConfig>() ?? new();
    }

    public InputRegistryEntry? ResolveInput(string deviceId, string slotId)
    {
        if (!_config.Devices.TryGetValue(deviceId, out var device))
            return null;

        if (!device.Inputs.TryGetValue(slotId, out var input))
            return null;

        var playerId = input.PlayerId ?? device.DefaultPlayerId;
        if (playerId is null)
            return null;

        return new InputRegistryEntry(
            PlayerId: playerId,
            InputName: input.InputName,
            InputType: input.Type,
            MinRaw: input.MinRaw,
            MaxRaw: input.MaxRaw
        );
    }

    public float NormalizeValue(InputRegistryEntry entry, float raw)
    {
        if (entry.MinRaw is null || entry.MaxRaw is null)
            return raw;

        var range = entry.MaxRaw.Value - entry.MinRaw.Value;
        if (range == 0) return 0f;

        return Math.Clamp((raw - entry.MinRaw.Value) / range, 0f, 1f);
    }
}