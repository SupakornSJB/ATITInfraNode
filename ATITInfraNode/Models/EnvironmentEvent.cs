namespace ATITInfraNode.Models;

public record EnvironmentEvent(string EnvKey, float RawValue, long Timestamp);