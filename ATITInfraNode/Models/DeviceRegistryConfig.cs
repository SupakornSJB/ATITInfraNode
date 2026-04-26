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
    public string InputRole { get; init; } = "player";  // "player", "environment", "stage"
    public string? EnvKey { get; init; }
    public Dictionary<string, string> AuxiliaryKeys { get; init; } = new();  // payload key → envKey
}

public record InputRegistryEntry(
    string? PlayerId,   // null for environment inputs
    string InputName,
    string InputType,
    float? MinRaw,
    float? MaxRaw,
    string InputRole,
    string? EnvKey,
    Dictionary<string, string> AuxiliaryKeys
);