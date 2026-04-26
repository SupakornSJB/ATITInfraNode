using ATITInfraNode.Endpoints;
using ATITInfraNode.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DeviceRegistryService>();
builder.Services.AddSingleton<UnityRelayService>();
builder.Services.AddSingleton<ZoneResolverService>();
builder.Services.AddSingleton<InputBufferService>();
builder.Services.AddHostedService<InputBufferService>(sp => sp.GetRequiredService<InputBufferService>());
builder.Services.AddHostedService<MqttSubscriberService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("healthy"));
app.MapAdminEndpoints();

app.Run();