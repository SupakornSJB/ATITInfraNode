using System.Text;
using System.Text.Json;
using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;

namespace IoTGame.Core.Services;

public class MqttSubscriberService : IHostedService
{
    private readonly IMqttClient _mqttClient;
    private readonly IoTGameOptions _options;
    private readonly IDeviceAuthenticator _deviceAuth;
    private readonly ISessionResolver _sessionResolver;
    private readonly IPlayerMapper _playerMapper;
    private readonly InputBufferService _inputBuffer;
    private readonly UnityRelayService _unityRelay;
    private readonly ZoneResolverService _zoneResolver;
    private readonly ILogger<MqttSubscriberService> _logger;

    public MqttSubscriberService(
        IoTGameOptions options,
        IDeviceAuthenticator deviceAuth,
        ISessionResolver sessionResolver,
        IPlayerMapper playerMapper,
        InputBufferService inputBuffer,
        UnityRelayService unityRelay,
        ZoneResolverService zoneResolver,
        ILogger<MqttSubscriberService> logger)
    {
        _options = options;
        _deviceAuth = deviceAuth;
        _sessionResolver = sessionResolver;
        _playerMapper = playerMapper;
        _inputBuffer = inputBuffer;
        _unityRelay = unityRelay;
        _zoneResolver = zoneResolver;
        _logger = logger;
        _mqttClient = new MqttFactory().CreateMqttClient();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const string topicFilter = "game/devices/+/slot/+";

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.MqttHost, _options.MqttPort)
            .WithCredentials(_options.MqttUser, _options.MqttPassword);

        if (_options.MqttPort == 8883)
            optionsBuilder = optionsBuilder.WithTlsOptions(o => o.UseTls());

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;

        await _mqttClient.ConnectAsync(optionsBuilder.Build(), cancellationToken);
        _logger.LogInformation("Connected to MQTT broker at {Host}:{Port}", _options.MqttHost, _options.MqttPort);

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
        var slotId   = segments[4];
        var payload  = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

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
            _logger.LogWarning("Null payload on topic {Topic}", topic);
            return;
        }

        // 1. Resolve which session owns this device (server's responsibility, not firmware's)
        var context = await _sessionResolver.ResolveAsync(deviceId);
        if (context is null)
        {
            _logger.LogWarning("No active session found for device {DeviceId} — dropping", deviceId);
            return;
        }

        // 2. Authenticate device against resolved session
        if (!await _deviceAuth.IsAuthenticatedAsync(deviceId, context.SessionId))
        {
            _logger.LogWarning("Device {DeviceId} not authenticated for session {SessionId} — dropping", deviceId, context.SessionId);
            return;
        }

        // 3. Map device/slot to player
        var mapping = await _playerMapper.GetPlayerMappingAsync(deviceId, slotId, context.SessionId);
        if (mapping is null)
        {
            _logger.LogWarning("No mapping for {DeviceId}/{SlotId} in session {SessionId} — dropping", deviceId, slotId, context.SessionId);
            return;
        }

        var endpoint = context.Endpoint;

        // 4. Route by inputRole
        switch (mapping.InputRole)
        {
            case "environment":
                await HandleEnvironmentEvent(endpoint, mapping, raw);
                break;

            case "stage":
                var stageValue = NormalizeValue(mapping, raw.Value);
                await _inputBuffer.EnqueueAsync(endpoint, new InputEvent(
                    PlayerId: "stage",
                    DeviceId: deviceId,
                    SlotId: slotId,
                    InputName: mapping.InputName ?? "",
                    InputType: raw.InputType,
                    EventType: raw.Event,
                    Value: stageValue,
                    Timestamp: raw.Timestamp,
                    SequenceNumber: raw.SequenceNumber
                ));
                break;

            default: // "player"
                if (mapping.PlayerId is null)
                {
                    _logger.LogWarning("No playerId for player-role slot {DeviceId}/{SlotId} — dropping", deviceId, slotId);
                    return;
                }
                var playerValue = NormalizeValue(mapping, raw.Value);
                await _inputBuffer.EnqueueAsync(endpoint, new InputEvent(
                    PlayerId: mapping.PlayerId,
                    DeviceId: deviceId,
                    SlotId: slotId,
                    InputName: mapping.InputName ?? "",
                    InputType: raw.InputType,
                    EventType: raw.Event,
                    Value: playerValue,
                    Timestamp: raw.Timestamp,
                    SequenceNumber: raw.SequenceNumber
                ));
                break;
        }
    }

    private async Task HandleEnvironmentEvent(string endpoint, PlayerMapping mapping, RawSlotPayload raw)
    {
        if (mapping.EnvKey is null)
        {
            _logger.LogWarning("Environment input has no envKey configured — dropping");
            return;
        }

        float valueToSend = raw.Value;

        if (mapping.EnvKey == "ultrasonic_zone")
        {
            var zone = _zoneResolver.Resolve(raw.Value);
            if (zone is null) return;
            valueToSend = zone.Value;
        }

        await _unityRelay.SendEnvironmentEvent(endpoint, new EnvironmentEvent(
            EnvKey: mapping.EnvKey,
            RawValue: valueToSend,
            Timestamp: raw.Timestamp
        ));

        if (mapping.AuxiliaryKeys is null || raw.Auxiliary is null) return;
        foreach (var (payloadKey, envKey) in mapping.AuxiliaryKeys)
        {
            if (!raw.Auxiliary.TryGetValue(payloadKey, out var auxValue)) continue;
            await _unityRelay.SendEnvironmentEvent(endpoint, new EnvironmentEvent(
                EnvKey: envKey,
                RawValue: auxValue,
                Timestamp: raw.Timestamp
            ));
        }
    }

    private static float NormalizeValue(PlayerMapping mapping, float raw)
    {
        if (mapping.MinRaw is null || mapping.MaxRaw is null) return raw;
        var range = mapping.MaxRaw.Value - mapping.MinRaw.Value;
        if (range == 0) return 0f;
        return Math.Clamp((raw - mapping.MinRaw.Value) / range, 0f, 1f);
    }

    private record RawSlotPayload(
        string  DeviceId,
        string  SlotId,
        string  InputType,
        string  Event,
        float   Value,
        long    Timestamp,
        long    SequenceNumber,
        Dictionary<string, float>? Auxiliary = null
    );
}
