using IoTGame.Core.Abstractions;

namespace IoTGame.Core.Auth;

public class AllowAllDeviceAuthenticator : IDeviceAuthenticator
{
    public Task<bool> IsAuthenticatedAsync(string deviceId, string sessionId) =>
        Task.FromResult(true);
}
