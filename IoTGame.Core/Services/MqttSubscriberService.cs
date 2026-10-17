using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;
using AlertType = IoTGame.Core.Models.AlertType;
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
    private readonly IIntrusionDetector _ids;
    private readonly ILogger<MqttSubscriberService> _logger;

    public MqttSubscriberService(
        IoTGameOptions options,
        IDeviceAuthenticator deviceAuth,
        ISessionResolver sessionResolver,
        IPlayerMapper playerMapper,
        InputBufferService inputBuffer,
        UnityRelayService unityRelay,
        ZoneResolverService zoneResolver,
        IIntrusionDetector ids,
        ILogger<MqttSubscriberService> logger)
    {
        _options = options;
        _deviceAuth = deviceAuth;
        _sessionResolver = sessionResolver;
        _playerMapper = playerMapper;
        _inputBuffer = inputBuffer;
        _unityRelay = unityRelay;
        _zoneResolver = zoneResolver;
        _ids = ids;
        _logger = logger;
        _mqttClient = new MqttFactory().CreateMqttClient();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const string topicFilter = "game/devices/+/slot/+";

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.MqttHost, _options.MqttPort)
            .WithCredentials(_options.MqttUser, _options.MqttPassword);

        if (_options.MqttUseTls || _options.MqttPort == 8883)
        {
            optionsBuilder = optionsBuilder.WithTlsOptions(o =>
            {
                o.UseTls();

                if (!string.IsNullOrEmpty(_options.MqttCaFile))
                {
                    var caCert = new X509Certificate2(_options.MqttCaFile);
                    o.WithCertificateValidationHandler(ctx =>
                    {
                        var chain = new X509Chain();
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(caCert);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        return chain.Build(new X509Certificate2(ctx.Certificate));
                    });
                }

                if (!string.IsNullOrEmpty(_options.MqttClientCertFile) &&
                    !string.IsNullOrEmpty(_options.MqttClientKeyFile))
                {
                    var pemCert = X509Certificate2.CreateFromPemFile(
                        _options.MqttClientCertFile, _options.MqttClientKeyFile);
                    // Re-import via PKCS12 so Windows SChannel can access the private key
                    var clientCert = new X509Certificate2(pemCert.Export(X509ContentType.Pkcs12));
                    o.WithClientCertificates(new List<X509Certificate2> { clientCert });
                }
            });
        }

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
        _logger.LogInformation("[MQTT] Message received on topic: {Topic}", topic);

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
        _logger.LogInformation("[MQTT] device={DeviceId} slot={SlotId} payload={Payload}", deviceId, slotId, payload);

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

        // 1. Check quarantine before any processing
        if (_ids.IsQuarantined(deviceId))
        {
            _logger.LogWarning("[IDS] Device {DeviceId} is quarantined — dropping event on slot {SlotId}", deviceId, slotId);
            return;
        }

        // 2. Resolve which session owns this device
        var context = await _sessionResolver.ResolveAsync(deviceId);
        if (context is null)
        {
            _logger.LogWarning("No active session found for device {DeviceId} — dropping", deviceId);
            await _ids.ReportAsync(new IntrusionAlert
            {
                DeviceId  = deviceId,
                AlertType = AlertType.UnknownDevice,
                Detail    = $"No active session for device {deviceId}",
                Timestamp = DateTimeOffset.UtcNow
            });
            return;
        }

        // 3. Authenticate device against resolved session
        if (!await _deviceAuth.IsAuthenticatedAsync(deviceId, context.SessionId))
        {
            _logger.LogWarning("Device {DeviceId} not authenticated for session {SessionId} — dropping", deviceId, context.SessionId);
            await _ids.ReportAsync(new IntrusionAlert
            {
                DeviceId         = deviceId,
                AlertType        = AlertType.UnauthorizedDevice,
                Detail           = $"Auth failed for session {context.SessionId}",
                Timestamp        = DateTimeOffset.UtcNow,
                ShouldQuarantine = true
            });
            return;
        }

        // Record device as seen and get current rate
        var rate = await _ids.TrackDeviceAsync(deviceId, raw.SequenceNumber);
        if (_options.MaxEventsPerSecond > 0 && rate > _options.MaxEventsPerSecond)
        {
            _logger.LogWarning("[IDS] Rate {Rate:F0}/s exceeds limit {Limit}/s for {DeviceId} — quarantining",
                rate, _options.MaxEventsPerSecond, deviceId);
            await _ids.ReportAsync(new IntrusionAlert
            {
                DeviceId         = deviceId,
                AlertType        = AlertType.RateExceeded,
                Detail           = $"Rate {rate:F0}/s exceeds limit of {_options.MaxEventsPerSecond}/s",
                Timestamp        = DateTimeOffset.UtcNow,
                ShouldQuarantine = true
            });
            return;
        }

        // 4. Map device/slot to player
        var mapping = await _playerMapper.GetPlayerMappingAsync(deviceId, slotId, context.SessionId);
        if (mapping is null)
        {
            _logger.LogWarning("No mapping for {DeviceId}/{SlotId} in session {SessionId} — dropping", deviceId, slotId, context.SessionId);
            await _ids.ReportAsync(new IntrusionAlert
            {
                DeviceId  = deviceId,
                AlertType = AlertType.UnknownSlot,
                Detail    = $"Unknown slot '{slotId}' on device {deviceId}",
                Timestamp = DateTimeOffset.UtcNow
            });
            return;
        }

        var endpoint = context.Endpoint;

        _logger.LogInformation("[MQTT] Routing device={DeviceId} slot={SlotId} role={Role} inputName={InputName}",
            deviceId, slotId, mapping.InputRole, mapping.InputName);

        // 5. Route by inputRole
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
