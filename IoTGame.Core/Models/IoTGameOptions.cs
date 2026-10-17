namespace IoTGame.Core.Models;

public class IoTGameOptions
{
    public string MqttHost       { get; set; } = "localhost";
    public int    MqttPort       { get; set; } = 8883;
    public string? MqttUser      { get; set; }
    public string? MqttPassword  { get; set; }
    public int    BufferWindowMs { get; set; } = 0;
    public int    MaxEventsPerSecond { get; set; } = 60;
    public string? AdminApiKey   { get; set; }
    public bool    MqttUseTls        { get; set; } = false;
    public string? MqttCaFile        { get; set; }
    public string? MqttClientCertFile { get; set; }
    public string? MqttClientKeyFile  { get; set; }

    // Client cert presented to Unity's gRPC server (mutual TLS)
    public string? GrpcClientCertFile { get; set; }
    public string? GrpcClientKeyFile  { get; set; }

    // Fallback Unity endpoint used by admin commands when no sessions exist in the store
    public string? DefaultUnityEndpoint { get; set; }
}
