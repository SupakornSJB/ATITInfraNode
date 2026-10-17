using IoTGame.Core.Abstractions;
using IoTGame.Core.Auth;
using IoTGame.Core.Models;
using IoTGame.Core.Services;
using IoTGame.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTGame.Core.Extensions;

public static class IoTGameServiceCollectionExtensions
{
    public static IoTGameBuilder AddIoTGame(
        this IServiceCollection services,
        Action<IoTGameOptions> configure)
    {
        var options = new IoTGameOptions();
        configure(options);
        services.AddSingleton(options);

        // Core services
        services.AddSingleton<UnityRelayService>();
        services.AddSingleton<ZoneResolverService>();
        services.AddSingleton<InputBufferService>();
        services.AddSingleton<GameStateService>();
        services.AddHostedService<InputBufferService>(sp => sp.GetRequiredService<InputBufferService>());
        services.AddHostedService<MqttSubscriberService>();
        services.AddSingleton<SessionManager>();

        // Defaults — overridable via builder methods
        services.AddSingleton<ISessionStore, InMemorySessionStore>();
        services.AddSingleton<IDeviceAuthenticator, AllowAllDeviceAuthenticator>();
        services.AddSingleton<IAdminAuthenticator, ApiKeyAdminAuthenticator>();
        services.AddSingleton<ISessionResolver, SessionStoreResolver>();
        services.AddSingleton<IPlayerMapper, SessionStorePlayerMapper>();
        services.AddSingleton<IIntrusionDetector, NoOpIntrusionDetector>();

        // Null token service fallback so endpoints don't throw when token auth is disabled
        services.AddSingleton<SessionTokenService>();

        return new IoTGameBuilder(services);
    }
}
