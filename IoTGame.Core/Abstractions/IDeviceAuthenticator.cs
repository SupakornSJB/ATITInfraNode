namespace IoTGame.Core.Abstractions;

public interface IDeviceAuthenticator
{
    Task<bool> IsAuthenticatedAsync(string deviceId, string sessionId);
}
