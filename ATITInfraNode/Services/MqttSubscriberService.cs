using System.Text;
using System.Text.Json;
using ATITInfraNode.Models;
using MQTTnet;
using MQTTnet.Client;

namespace ATITInfraNode.Services;

public class MqttSubscriberService : IHostedService
{
    private readonly IMqttClient _mqttClient;
    private readonly IConfiguration _configuration;
    private readonly DeviceRegistryService _deviceRegistry;
    private readonly InputBufferService _inputBuffer;
    private readonly UnityRelayService _unityRelay;
    private readonly ZoneResolverService _zoneResolver;
    private readonly ILogger<MqttSubscriberService> _logger;

    public MqttSubscriberService(
        IConfiguration configuration,
        DeviceRegistryService deviceRegistry,
        InputBufferService inputBuffer,
        UnityRelayService unityRelay,
        ZoneResolverService zoneResolver,
        ILogger<MqttSubscriberService> logger)
    {
        _configuration = configuration;
        _deviceRegistry = deviceRegistry;
        _inputBuffer = inputBuffer;
        _unityRelay = unityRelay;
        _zoneResolver = zoneResolver;
        _logger = logger;
        _mqttClient = new MqttFactory().CreateMqttClient();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var host = _configuration["Mqtt:Host"] ?? throw new InvalidOperationException("Mqtt:Host is not configured");
        var port = _configuration.GetValue<int>("Mqtt:Port", 8883);
        var username = _configuration["Mqtt:Username"];
        var password = _configuration["Mqtt:Password"];
        const string topicFilter = "game/devices/+/slot/+";

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithCredentials(username, password);

        if (port == 8883)
            optionsBuilder = optionsBuilder.WithTlsOptions(o => o.UseTls());

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;

        await _mqttClient.ConnectAsync(optionsBuilder.Build(), cancellationToken);
        _logger.LogInformation("Connected to MQTT broker at {Host}:{Port}", host, port);

        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(topicFilter))
            .Build();
        await _mqttClient.SubscribeAsync(subscribeOptions, cancellationToken);
        _logger.LogInformation("Subscribed to topic: {Topic}", topicFilter);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
        _logger.LogInformation("Disconnected from MQTT broker");
    }

    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segments = topic.Split('/');

        // Expected: game/devices/{deviceId}/slot/{slotId}
        if (segments.Length != 5)
        {
            _logger.LogWarning("Unexpected topic format: {Topic}", topic);
            return;
        }

        var deviceId = segments[2];
        var slotId = segments[4];

        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

        RawSlotPayload? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawSlotPayload>(payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize MQTT payload: {Payload}", payload);
            return;
        }

        if (raw is null)
        {
            _logger.LogWarning("Received null payload on topic {Topic}", topic);
            return;
        }

        var entry = _deviceRegistry.ResolveInput(deviceId, slotId);
        if (entry is null)
        {
            _logger.LogWarning("Dropping event from unregistered device/slot: {DeviceId}/{SlotId}", deviceId, slotId);
            return;
        }

        switch (entry.InputRole)
        {
            case "environment":
                await HandleEnvironmentEvent(entry, raw);
                break;

            case "stage":
                var stageValue = _deviceRegistry.NormalizeValue(entry, raw.Value);
                var stageEvent = new InputEvent(
                    PlayerId: "stage",
                    DeviceId: deviceId,
                    SlotId: slotId,
                    InputName: entry.InputName,
                    InputType: raw.InputType,
                    EventType: raw.Event,
                    Value: stageValue,
                    Timestamp: raw.Timestamp,
                    SequenceNumber: raw.SequenceNumber,
                    Auxiliary: raw.Auxiliary ?? new()
                );
                await _inputBuffer.EnqueueAsync(stageEvent);
                break;

            default: // "player"
                var playerValue = _deviceRegistry.NormalizeValue(entry, raw.Value);
                var playerEvent = new InputEvent(
                    PlayerId: entry.PlayerId!,
                    DeviceId: deviceId,
                    SlotId: slotId,
                    InputName: entry.InputName,
                    InputType: raw.InputType,
                    EventType: raw.Event,
                    Value: playerValue,
                    Timestamp: raw.Timestamp,
                    SequenceNumber: raw.SequenceNumber,
                    Auxiliary: raw.Auxiliary ?? new()
                );
                await _inputBuffer.EnqueueAsync(playerEvent);
                break;
        }
    }

    private async Task HandleEnvironmentEvent(InputRegistryEntry entry, RawSlotPayload raw)
    {
        if (entry.EnvKey is null)
        {
            _logger.LogWarning("Environment input has no envKey configured: {InputName}", entry.InputName);
            return;
        }

        float valueToSend = raw.Value;

        if (entry.EnvKey == "ultrasonic_zone")
        {
            var zone = _zoneResolver.Resolve(raw.Value);
            if (zone is null) return; // no zone change — suppress
            valueToSend = zone.Value;
        }

        await _unityRelay.SendEnvironmentEvent(new EnvironmentEvent(
            EnvKey: entry.EnvKey,
            RawValue: valueToSend,
            Timestamp: raw.Timestamp
        ));

        // Forward each auxiliary reading as its own environment event
        if (raw.Auxiliary is null) return;
        foreach (var (payloadKey, auxValue) in raw.Auxiliary)
        {
            if (!entry.AuxiliaryKeys.TryGetValue(payloadKey, out var auxEnvKey))
            {
                _logger.LogDebug("Unknown auxiliary key '{Key}' on {DeviceId}/{SlotId} — ignored", payloadKey, raw.DeviceId, raw.SlotId);
                continue;
            }
            await _unityRelay.SendEnvironmentEvent(new EnvironmentEvent(
                EnvKey: auxEnvKey,
                RawValue: auxValue,
                Timestamp: raw.Timestamp
            ));
        }
    }

    // Raw firmware payload — no semantic meaning, just physical slot data
    private record RawSlotPayload(
        string DeviceId,
        string SlotId,
        string InputType,
        string Event,
        float Value,
        long Timestamp,
        long SequenceNumber,
        Dictionary<string, float>? Auxiliary = null
    );
}