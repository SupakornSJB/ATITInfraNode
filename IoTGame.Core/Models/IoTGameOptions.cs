namespace IoTGame.Core.Models;

public class IoTGameOptions
{
    public string MqttHost       { get; set; } = "localhost";
    public int    MqttPort       { get; set; } = 8883;
    public string? MqttUser      { get; set; }
    public string? MqttPassword  { get; set; }
    public string? SharedSecret  { get; set; }
    public int    BufferWindowMs { get; set; } = 0;
    public int    MaxEventsPerSecond { get; set; } = 60;
    public string? AdminApiKey   { get; set; }
}
