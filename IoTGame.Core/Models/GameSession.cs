namespace IoTGame.Core.Models;

public class GameSession
{
    public string        SessionId     { get; init; } = Guid.NewGuid().ToString();
    public string        UnityEndpoint { get; init; } = "";
    public SessionStatus Status        { get; set; }  = SessionStatus.Created;
    public DateTime      CreatedAt     { get; init; } = DateTime.UtcNow;
    public DateTime?     StartedAt     { get; set; }
    public DateTime?     EndedAt       { get; set; }
    public List<SessionDeviceConfig> Devices { get; init; } = new();
}

public enum SessionStatus { Created, Active, Ended }
