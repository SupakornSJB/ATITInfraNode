namespace IoTGame.Core.Models;

public record PlayerMapping(
    string? PlayerId,
    string? InputName,
    string  InputRole,
    string? EnvKey,
    float?  MinRaw,
    float?  MaxRaw,
    Dictionary<string, string>? AuxiliaryKeys
);
