using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameServer.Hubs;

[Authorize]
public class SecurityHub : Hub
{
    // Server-to-client push only — no client-to-server methods needed.
    // Push method names (called from RuleBasedIntrusionDetector and SecurityController):
    //   OnAlert(IntrusionAlert)
    //   OnDeviceStateChanged(DeviceSecurityState)
    //
    // React client connects with JWT via query string (WebSocket limitation):
    //   new HubConnectionBuilder()
    //     .withUrl("/hubs/security", { accessTokenFactory: () => token })
    //     .build()
}
