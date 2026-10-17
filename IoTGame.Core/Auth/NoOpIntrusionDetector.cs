using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;

namespace IoTGame.Core.Auth;

public class NoOpIntrusionDetector : IIntrusionDetector
{
    public Task ReportAsync(IntrusionAlert alert) => Task.CompletedTask;
    public Task<double> TrackDeviceAsync(string deviceId, long? sequenceNumber = null) => Task.FromResult(0.0);
    public void Unquarantine(string deviceId) { }
    public void ClearAlerts() { }
    public bool IsQuarantined(string deviceId) => false;
    public IReadOnlyList<DeviceSecurityState> GetAllDeviceStates() => [];
    public IReadOnlyList<IntrusionAlert> GetRecentAlerts(int max = 100) => [];
}
