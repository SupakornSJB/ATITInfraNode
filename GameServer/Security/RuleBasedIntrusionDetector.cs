using GameServer.Hubs;
using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace GameServer.Security;

public class RuleBasedIntrusionDetector : IIntrusionDetector
{
    private readonly IHubContext<SecurityHub> _hub;
    private readonly ILogger<RuleBasedIntrusionDetector> _logger;

    private readonly object _stateLock = new();
    private readonly Dictionary<string, DeviceSecurityState> _deviceStates = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset WindowStart, double LastRate)> _rateWindows = new();
    private readonly List<IntrusionAlert> _alerts = new();

    public RuleBasedIntrusionDetector(
        IHubContext<SecurityHub> hub,
        ILogger<RuleBasedIntrusionDetector> logger)
    {
        _hub    = hub;
        _logger = logger;
    }

    public async Task ReportAsync(IntrusionAlert alert)
    {
        _logger.LogWarning("[IDS] {AlertType} — device={DeviceId}: {Detail}",
            alert.AlertType, alert.DeviceId, alert.Detail);

        DeviceSecurityState state;
        lock (_stateLock)
        {
            if (!_deviceStates.TryGetValue(alert.DeviceId, out state!))
            {
                state = new DeviceSecurityState { DeviceId = alert.DeviceId };
                _deviceStates[alert.DeviceId] = state;
            }

            state.LastSeen = alert.Timestamp;
            state.AlertCount++;

            if (alert.ShouldQuarantine && !state.IsQuarantined)
            {
                state.IsQuarantined  = true;
                state.QuarantinedAt  = alert.Timestamp;
                _logger.LogWarning("[IDS] Device {DeviceId} quarantined", alert.DeviceId);
            }

            _alerts.Add(alert);
            if (_alerts.Count > 500)
                _alerts.RemoveRange(0, _alerts.Count - 500);
        }

        await _hub.Clients.All.SendAsync("OnAlert", alert);
        await _hub.Clients.All.SendAsync("OnDeviceStateChanged", state);
    }

    public async Task<double> TrackDeviceAsync(string deviceId, long? sequenceNumber = null)
    {
        DeviceSecurityState state;
        double rate;
        lock (_stateLock)
        {
            if (!_deviceStates.TryGetValue(deviceId, out state!))
            {
                state = new DeviceSecurityState { DeviceId = deviceId };
                _deviceStates[deviceId] = state;
            }
            state.LastSeen = DateTimeOffset.UtcNow;
            if (sequenceNumber.HasValue)
                state.LastSequenceNumber = sequenceNumber;

            var now = DateTimeOffset.UtcNow;
            if (!_rateWindows.TryGetValue(deviceId, out var window) ||
                (now - window.WindowStart).TotalSeconds >= 1.0)
            {
                // Window just completed — its count IS the events/sec for that second
                var completedRate = window.Count > 0
                    ? window.Count / Math.Max((now - window.WindowStart).TotalSeconds, 1.0)
                    : 0;
                rate = completedRate;
                _rateWindows[deviceId] = (1, now, completedRate);
            }
            else
            {
                _rateWindows[deviceId] = (window.Count + 1, window.WindowStart, window.LastRate);
                // Show previous window's rate while current window is still accumulating
                rate = window.LastRate;
            }

            state.CurrentRate = rate;
        }
        await _hub.Clients.All.SendAsync("OnDeviceStateChanged", state);
        return rate;
    }

    public void Unquarantine(string deviceId)
    {
        lock (_stateLock)
        {
            if (_deviceStates.TryGetValue(deviceId, out var state))
            {
                state.IsQuarantined = false;
                state.QuarantinedAt = null;
            }
        }
        _logger.LogInformation("[IDS] Device {DeviceId} unquarantined", deviceId);
    }

    public void ClearAlerts()
    {
        lock (_stateLock)
        {
            _alerts.Clear();
        }
    }

    public bool IsQuarantined(string deviceId)
    {
        lock (_stateLock)
        {
            return _deviceStates.TryGetValue(deviceId, out var state) && state.IsQuarantined;
        }
    }

    public IReadOnlyList<DeviceSecurityState> GetAllDeviceStates()
    {
        lock (_stateLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (id, state) in _deviceStates)
            {
                if (_rateWindows.TryGetValue(id, out var w))
                    // If no events for >2 seconds, rate has decayed to 0
                    state.CurrentRate = (now - w.WindowStart).TotalSeconds > 2.0 ? 0 : w.LastRate;
                else
                    state.CurrentRate = 0;
            }
            return _deviceStates.Values.ToList().AsReadOnly();
        }
    }

    public IReadOnlyList<IntrusionAlert> GetRecentAlerts(int max = 100)
    {
        lock (_stateLock)
        {
            return _alerts.TakeLast(max).ToList().AsReadOnly();
            
        }
    }
}
