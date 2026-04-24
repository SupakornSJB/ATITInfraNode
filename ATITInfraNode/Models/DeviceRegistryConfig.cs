namespace ATITInfraNode.Models;

public class DeviceRegistryConfig
{
    public Dictionary<string, List<string>> Players { get; init; } = new();
    public Dictionary<string, DeviceConfig> Devices { get; init; } = new();
}

public class DeviceConfig
{
    public string? DefaultPlayerId { get; init; }
    public Dictionary<string, InputConfig> Inputs { get; init; } = new();
}

public class InputConfig
{
    public string Type { get; init; } = "";
    public string InputName { get; init; } = "";  // semantic game label sent to Unity
    public string? PlayerId { get; init; }
    public float? MinRaw { get; init; }
    public float? MaxRaw { get; init; }
}

public record InputRegistryEntry(
    string PlayerId,
    string InputName,   // semantic — forwarded to Unity
    string InputType,
    float? MinRaw,
    float? MaxRaw
);