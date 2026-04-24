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
    private readonly ILogger<MqttSubscriberService> _logger;

    public MqttSubscriberService(
        IConfiguration configuration,
        DeviceRegistryService deviceRegistry,
        InputBufferService inputBuffer,
        ILogger<MqttSubscriberService> logger)
    {
        _configuration = configuration;
        _deviceRegistry = deviceRegistry;
        _inputBuffer = inputBuffer;
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

        var normalizedValue = _deviceRegistry.NormalizeValue(entry, raw.Value);

        var inputEvent = new InputEvent(
            PlayerId: entry.PlayerId,
            DeviceId: deviceId,
            SlotId: slotId,
            InputName: entry.InputName,
            InputType: raw.InputType,
            EventType: raw.Event,
            Value: normalizedValue,
            Timestamp: raw.Timestamp,
            SequenceNumber: raw.SequenceNumber
        );

        await _inputBuffer.EnqueueAsync(inputEvent);
    }

    // Raw firmware payload — no semantic meaning, just physical slot data
    private record RawSlotPayload(
        string DeviceId,
        string SlotId,
        string InputType,
        string Event,
        float Value,
        long Timestamp,
        long SequenceNumber
    );
}