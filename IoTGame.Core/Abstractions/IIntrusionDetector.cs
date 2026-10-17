using IoTGame.Core.Models;

namespace IoTGame.Core.Abstractions;

public interface IIntrusionDetector
{
    Task ReportAsync(IntrusionAlert alert);
    // Returns current events/sec after recording this event
    Task<double> TrackDeviceAsync(string deviceId, long? sequenceNumber = null);
    void Unquarantine(string deviceId);
    void ClearAlerts();
    bool IsQuarantined(string deviceId);
    IReadOnlyList<DeviceSecurityState> GetAllDeviceStates();
    IReadOnlyList<IntrusionAlert> GetRecentAlerts(int max = 100);
}
