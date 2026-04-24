namespace ATITInfraNode.Models;

// Fully resolved internal event — registry lookup already applied
public record InputEvent(
    string PlayerId,        // resolved from registry
    string DeviceId,        // from payload (retained for logging/rate-limiting)
    string SlotId,          // from payload (retained for logging/rate-limiting)
    string InputName,       // resolved from registry — this is what Unity sees
    string InputType,       // from payload
    string EventType,
    float  Value,           // normalized if knob
    long   Timestamp,
    long   SequenceNumber
);