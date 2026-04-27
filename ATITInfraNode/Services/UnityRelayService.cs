using ATITInfraNode.Models;
using GameController.V2;
using Grpc.Core;
using Grpc.Net.Client;

namespace ATITInfraNode.Services;

public class UnityRelayService
{
    private GrpcChannel _channel;
    private GameControllerService.GameControllerServiceClient _client;
    private readonly string _endpoint;
    private readonly ILogger<UnityRelayService> _logger;

    public UnityRelayService(IConfiguration configuration, ILogger<UnityRelayService> logger)
    {
        _logger = logger;
        _endpoint = configuration["Unity:GrpcEndpoint"] ?? "http://localhost:9090";
        _channel = BuildChannel(_endpoint);
        _client  = new GameControllerService.GameControllerServiceClient(_channel);
    }

    private static GrpcChannel BuildChannel(string endpoint)
    {
        if (endpoint.StartsWith("http://"))
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        return GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                KeepAlivePingDelay             = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout           = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true
            }
        });
    }

    private async Task EnsureConnectedAsync()
    {
        var state = _channel.State;
        if (state is ConnectivityState.TransientFailure or ConnectivityState.Shutdown)
        {
            _logger.LogWarning("[gRPC] Channel in {State} — rebuilding", state);
            await _channel.ShutdownAsync();
            _channel = BuildChannel(_endpoint);
            _client  = new GameControllerService.GameControllerServiceClient(_channel);
        }
    }

    public async Task SendInputBatch(IEnumerable<InputEventRequest> events)
    {
        await EnsureConnectedAsync();
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
        catch (RpcException ex)
        {
            _logger.LogError("[gRPC] SendInputBatch failed: {Status}", ex.Status);
        }
    }

    public async Task SendEnvironmentEvent(EnvironmentEvent evt)
    {
        await EnsureConnectedAsync();
        try
        {
            var request = new EnvironmentEventRequest
            {
                EnvKey    = evt.EnvKey,
                RawValue  = evt.RawValue,
                Timestamp = evt.Timestamp
            };

            _logger.LogInformation("Sending environment event: key={EnvKey} value={Value}", evt.EnvKey, evt.RawValue);
            var response = await _client.SendEnvironmentEventAsync(request);

            if (response.Accepted)
                _logger.LogInformation("Unity accepted environment event: {EnvKey}", evt.EnvKey);
            else
                _logger.LogWarning("Unity rejected environment event {EnvKey}: {Message}", evt.EnvKey, response.Message);
        }
        catch (RpcException ex)
        {
            _logger.LogError("[gRPC] SendEnvironmentEvent failed: {Status}", ex.Status);
        }
    }

    public async Task SendAdminCommand(string command, string issuedBy)
    {
        await EnsureConnectedAsync();
        try
        {
            var request = new AdminCommandRequest
            {
                Command   = command,
                IssuedBy  = issuedBy,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            _logger.LogInformation("Sending admin command: {Command} by {IssuedBy}", command, issuedBy);
            var response = await _client.SendAdminCommandAsync(request);

            if (response.Accepted)
                _logger.LogInformation("Unity accepted admin command: {Command}", command);
            else
                _logger.LogWarning("Unity rejected admin command {Command}: {Message}", command, response.Message);
        }
        catch (RpcException ex)
        {
            _logger.LogError("[gRPC] SendAdminCommand failed: {Status}", ex.Status);
        }
    }
}
