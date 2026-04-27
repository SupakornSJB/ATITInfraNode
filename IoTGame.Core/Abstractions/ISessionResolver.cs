using IoTGame.Core.Models;

namespace IoTGame.Core.Abstractions;

public interface ISessionResolver
{
    /// <summary>
    /// Resolves which session a device belongs to.
    /// Returns null if no active session owns this device → event dropped.
    /// </summary>
    Task<SessionContext?> ResolveAsync(string deviceId);
}
