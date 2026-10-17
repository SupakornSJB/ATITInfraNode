using GameServer.Hubs;
using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;
using IoTGame.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace GameServer.Controllers;

[ApiController]
[Route("api/security")]
[Authorize]
public class SecurityController : ControllerBase
{
    private readonly IIntrusionDetector       _ids;
    private readonly IHubContext<SecurityHub> _hub;
    private readonly GameStateService         _gameState;
    private readonly IConfiguration           _config;

    public SecurityController(
        IIntrusionDetector ids,
        IHubContext<SecurityHub> hub,
        GameStateService gameState,
        IConfiguration config)
    {
        _ids       = ids;
        _hub       = hub;
        _gameState = gameState;
        _config    = config;
    }

    // GET /api/security/status — full snapshot for dashboard initial load
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var registeredDevices = _config
            .GetSection("DeviceRegistry:devices")
            .GetChildren()
            .Select(d => d.Key)
            .ToList();

        return Ok(new
        {
            round = new
            {
                status    = _gameState.Status.ToString().ToLowerInvariant(),
                startedAt = _gameState.StartedAt,
                endedAt   = _gameState.EndedAt
            },
            devices           = _ids.GetAllDeviceStates(),
            registeredDevices,
            alerts            = _ids.GetRecentAlerts(),
            timestamp         = DateTimeOffset.UtcNow
        });
    }

    // GET /api/security/devices
    [HttpGet("devices")]
    public IActionResult GetDevices() => Ok(_ids.GetAllDeviceStates());

    // GET /api/security/alerts
    [HttpGet("alerts")]
    public IActionResult GetAlerts() => Ok(_ids.GetRecentAlerts());

    // POST /api/security/devices/{deviceId}/quarantine
    // Returns the full updated device state so the frontend can update without needing SignalR
    [HttpPost("devices/{deviceId}/quarantine")]
    public async Task<IActionResult> Quarantine(string deviceId)
    {
        await _ids.ReportAsync(new IntrusionAlert
        {
            DeviceId         = deviceId,
            AlertType        = AlertType.ManualQuarantine,
            Detail           = "Manually quarantined via dashboard",
            Timestamp        = DateTimeOffset.UtcNow,
            ShouldQuarantine = true
        });

        var state = _ids.GetAllDeviceStates().FirstOrDefault(d => d.DeviceId == deviceId)
                    ?? new DeviceSecurityState { DeviceId = deviceId };
        return Ok(state);
    }

    // POST /api/security/devices/{deviceId}/unquarantine
    [HttpPost("devices/{deviceId}/unquarantine")]
    public async Task<IActionResult> Unquarantine(string deviceId)
    {
        _ids.Unquarantine(deviceId);
        var state = _ids.GetAllDeviceStates().FirstOrDefault(d => d.DeviceId == deviceId)
                    ?? new DeviceSecurityState { DeviceId = deviceId };
        await _hub.Clients.All.SendAsync("OnDeviceStateChanged", state);
        return Ok(state);
    }

    // PUT /api/security/devices/{deviceId}/quarantine?quarantine=true|false
    // Single toggle endpoint so the frontend only needs one button
    [HttpPut("devices/{deviceId}/quarantine")]
    public async Task<IActionResult> SetQuarantine(string deviceId, [FromQuery] bool quarantine = true)
    {
        if (quarantine)
        {
            await _ids.ReportAsync(new IntrusionAlert
            {
                DeviceId         = deviceId,
                AlertType        = AlertType.ManualQuarantine,
                Detail           = "Manually quarantined via dashboard",
                Timestamp        = DateTimeOffset.UtcNow,
                ShouldQuarantine = true
            });
        }
        else
        {
            _ids.Unquarantine(deviceId);
            var updated = _ids.GetAllDeviceStates().FirstOrDefault(d => d.DeviceId == deviceId)
                          ?? new DeviceSecurityState { DeviceId = deviceId };
            await _hub.Clients.All.SendAsync("OnDeviceStateChanged", updated);
            return Ok(updated);
        }

        var state = _ids.GetAllDeviceStates().FirstOrDefault(d => d.DeviceId == deviceId)
                    ?? new DeviceSecurityState { DeviceId = deviceId };
        return Ok(state);
    }

    // POST /api/security/alerts/clear
    [HttpPost("alerts/clear")]
    public IActionResult ClearAlerts()
    {
        _ids.ClearAlerts();
        return Ok();
    }
}
