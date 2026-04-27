using GameServer.Mapping;
using IoTGame.Core.Auth;
using IoTGame.Core.Endpoints;
using IoTGame.Core.Extensions;
using IoTGame.Core.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIoTGame(options =>
{
    options.MqttHost           = builder.Configuration["Mqtt:Host"]     ?? "localhost";
    options.MqttPort           = builder.Configuration.GetValue<int>("Mqtt:Port", 8883);
    options.MqttUser           = builder.Configuration["Mqtt:Username"];
    options.MqttPassword       = builder.Configuration["Mqtt:Password"];
    options.SharedSecret       = builder.Configuration["IoTGame:SharedSecret"];
    options.BufferWindowMs     = builder.Configuration.GetValue<int>("IoTGame:BufferWindowMs", 0);
    options.MaxEventsPerSecond = builder.Configuration.GetValue<int>("IoTGame:MaxEventsPerSecond", 60);
    options.AdminApiKey        = builder.Configuration["IoTGame:AdminApiKey"];
})
.WithSessionStore<InMemorySessionStore>()
.WithDeviceAuthenticator<AllowAllDeviceAuthenticator>()
.WithAdminAuthenticator<ApiKeyAdminAuthenticator>()
// Static appsettings-based routing — no session management needed
.WithPlayerMapper<StaticDeviceRegistryMapper>()
.WithSessionResolver<StaticSessionResolver>()
.WithSessionTokenAuth();

var app = builder.Build();

app.MapIoTGameEndpoints();

app.Run();
