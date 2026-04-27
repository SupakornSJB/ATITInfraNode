using IoTGame.Core.Abstractions;
using IoTGame.Core.Auth;
using IoTGame.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace IoTGame.Core.Extensions;

public class IoTGameBuilder
{
    internal readonly IServiceCollection Services;

    internal IoTGameBuilder(IServiceCollection services) => Services = services;

    public IoTGameBuilder WithSessionStore<TStore>() where TStore : class, ISessionStore
    {
        Services.AddSingleton<ISessionStore, TStore>();
        return this;
    }

    public IoTGameBuilder WithDeviceAuthenticator<TAuth>() where TAuth : class, IDeviceAuthenticator
    {
        Services.AddSingleton<IDeviceAuthenticator, TAuth>();
        return this;
    }

    public IoTGameBuilder WithSessionResolver<TResolver>() where TResolver : class, ISessionResolver
    {
        Services.AddSingleton<ISessionResolver, TResolver>();
        return this;
    }

    public IoTGameBuilder WithPlayerMapper<TMapper>() where TMapper : class, IPlayerMapper
    {
        Services.AddSingleton<IPlayerMapper, TMapper>();
        return this;
    }

    public IoTGameBuilder WithAdminAuthenticator<TAuth>() where TAuth : class, IAdminAuthenticator
    {
        Services.AddSingleton<IAdminAuthenticator, TAuth>();
        return this;
    }

    public IoTGameBuilder WithSessionTokenAuth()
    {
        Services.AddSingleton<SessionTokenService>();
        return this;
    }
}
