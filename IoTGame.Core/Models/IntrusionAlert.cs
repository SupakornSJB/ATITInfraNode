namespace IoTGame.Core.Models;

public enum AlertType
{
    RateExceeded,
    UnknownSlot,
    UnknownDevice,
    UnauthorizedDevice,
    ManualQuarantine
}

public class IntrusionAlert
{
    public string        AlertId          { get; init; } = Guid.NewGuid().ToString();
    public string        DeviceId         { get; init; } = string.Empty;
    public AlertType     AlertType        { get; init; }
    public string        Detail           { get; init; } = string.Empty;
    public DateTimeOffset Timestamp       { get; init; } = DateTimeOffset.UtcNow;
    public bool          ShouldQuarantine { get; init; }
}
