namespace IoTGame.Core.Models;

public record InputEvent(
    string PlayerId,
    string DeviceId,
    string SlotId,
    string InputName,
    string InputType,
    string EventType,
    float  Value,
    long   Timestamp,
    long   SequenceNumber
);
