namespace IoTGame.Core.Models;

public class SessionDeviceConfig
{
    public string              DeviceId        { get; init; } = "";
    public string?             DefaultPlayerId { get; init; }
    public List<SlotConfig>    Slots           { get; init; } = new();
}

public class SlotConfig
{
    public string  SlotId    { get; init; } = "";
    public string  InputType { get; init; } = "";
    public string  InputRole { get; init; } = "player";  // "player", "environment", "stage"
    public string? PlayerId  { get; init; }
    public string? InputName { get; init; }
    public string? EnvKey    { get; init; }
    public float?  MinRaw    { get; init; }
    public float?  MaxRaw    { get; init; }
    public Dictionary<string, string>? AuxiliaryKeys { get; init; }
}
