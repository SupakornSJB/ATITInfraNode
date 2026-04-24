using GameController.V2;
using Grpc.Net.Client;

namespace ATITInfraNode.Services;

public class UnityRelayService
{
    private readonly GameControllerService.GameControllerServiceClient _client;
    private readonly ILogger<UnityRelayService> _logger;

    public UnityRelayService(IConfiguration configuration, ILogger<UnityRelayService> logger)
    {
        _logger = logger;
        var endpoint = configuration["Unity:GrpcEndpoint"] ?? "http://localhost:9090";
        var channelOptions = new GrpcChannelOptions();
        if (endpoint.StartsWith("http://"))
        {
            // Allow unencrypted HTTP/2 for local Unity dev server (no TLS cert)
            channelOptions.HttpHandler = new HttpClientHandler();
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }
        var channel = GrpcChannel.ForAddress(endpoint, channelOptions);
        _client = new GameControllerService.GameControllerServiceClient(channel);
    }

    public async Task SendInputBatch(IEnumerable<InputEventRequest> events)
    {
        try
        {
            var request = new InputBatchRequest();
            request.Events.AddRange(events);

            foreach (var e in request.Events)
                _logger.LogDebug(
                    "  → [{Seq}] player={PlayerId} device={DeviceId} input={InputName} ({InputType}) event={EventType} value={Value}",
                    e.SequenceNumber, e.PlayerId, e.DeviceId, e.InputName, e.InputType, e.EventType, e.Value);

            _logger.LogInformation("Sending {Count} event(s) to Unity", request.Events.Count);

            var response = await _client.SendInputBatchAsync(request);

            if (response.Accepted)
                _logger.LogInformation("Unity accepted {Count} event(s)", request.Events.Count);
            else
                _logger.LogWarning("Unity rejected batch: {Message}", response.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC SendInputBatch failed");
        }
    }
}