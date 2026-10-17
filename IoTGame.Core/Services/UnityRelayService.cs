using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using GameController.V2;
using Grpc.Core;
using Grpc.Net.Client;
using IoTGame.Core.Models;
using Microsoft.Extensions.Logging;

namespace IoTGame.Core.Services;

public class UnityRelayService
{
    private record ChannelEntry(GrpcChannel Channel, GameControllerService.GameControllerServiceClient Client);

    private readonly ConcurrentDictionary<string, ChannelEntry> _pool = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly string? _sharedSecret;
    private readonly ILogger<UnityRelayService> _logger;

    private readonly string? _grpcClientCertFile;
    private readonly string? _grpcClientKeyFile;

    public UnityRelayService(IoTGameOptions options, ILogger<UnityRelayService> logger)
    {
        _sharedSecret       = options.SharedSecret;
        _grpcClientCertFile = options.GrpcClientCertFile;
        _grpcClientKeyFile  = options.GrpcClientKeyFile;
        _logger = logger;
    }

    private static X509Certificate2 LoadCertForPlatform(string certFile, string keyFile)
    {
        var cert = X509Certificate2.CreateFromPemFile(certFile, keyFile);
        if (!OperatingSystem.IsWindows()) return cert;
        // Windows SChannel rejects ephemeral private keys from PEM imports.
        // A PFX round-trip registers the key with CNG so SChannel can use it.
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }

    private GrpcChannel BuildChannel(string endpoint)
    {
        if (endpoint.StartsWith("http://"))
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        bool isLocalHttps = endpoint.StartsWith("https://localhost") ||
                            endpoint.StartsWith("https://127.");

        HttpMessageHandler handler;

        if (isLocalHttps)
        {
            // SocketsHttpHandler fails on Windows localhost with ephemeral-key self-signed certs.
            // HttpClientHandler's built-in bypass handles this correctly.
            var h = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            if (!string.IsNullOrEmpty(_grpcClientCertFile) && !string.IsNullOrEmpty(_grpcClientKeyFile))
            {
                h.ClientCertificates.Add(LoadCertForPlatform(_grpcClientCertFile, _grpcClientKeyFile));
                _logger.LogInformation("[gRPC] Loaded client cert from {File}", _grpcClientCertFile);
            }
            handler = h;
        }
        else
        {
            // SocketsHttpHandler required for Channel.State / connectivity tracking on remote endpoints
            var sslOptions = new System.Net.Security.SslClientAuthenticationOptions();
            if (!string.IsNullOrEmpty(_grpcClientCertFile) && !string.IsNullOrEmpty(_grpcClientKeyFile))
            {
                sslOptions.ClientCertificates = new X509CertificateCollection
                    { LoadCertForPlatform(_grpcClientCertFile, _grpcClientKeyFile) };
                _logger.LogInformation("[gRPC] Loaded client cert from {File}", _grpcClientCertFile);
            }
            handler = new SocketsHttpHandler
            {
                KeepAlivePingDelay             = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout           = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true,
                SslOptions                     = sslOptions
            };
        }

        return GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler });
    }

    private async Task<ChannelEntry> EnsureConnectedAsync(string endpoint)
    {
        var entry = _pool.GetOrAdd(endpoint, ep =>
        {
            var ch = BuildChannel(ep);
            return new ChannelEntry(ch, new GameControllerService.GameControllerServiceClient(ch));
        });

        // HttpClientHandler (used for localhost) doesn't support Channel.State — skip check
        bool needsRebuild;
        try { needsRebuild = entry.Channel.State is ConnectivityState.TransientFailure or ConnectivityState.Shutdown; }
        catch (InvalidOperationException) { return entry; }

        if (!needsRebuild) return entry;

        // Serialize rebuilds per endpoint — prevents concurrent callers from double-disposing
        var gate = _locks.GetOrAdd(endpoint, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // Re-read after acquiring the lock; another caller may have already rebuilt
            entry = _pool[endpoint];
            bool stillBad;
            try { stillBad = entry.Channel.State is ConnectivityState.TransientFailure or ConnectivityState.Shutdown; }
            catch (InvalidOperationException) { return entry; }
            if (!stillBad) return entry;

            _logger.LogWarning("[gRPC] Channel to {Endpoint} in {State} — rebuilding", endpoint, entry.Channel.State);
            _ = entry.Channel.ShutdownAsync();   // fire-and-forget; don't await stale channel
            var ch = BuildChannel(endpoint);
            var newEntry = new ChannelEntry(ch, new GameControllerService.GameControllerServiceClient(ch));
            _pool[endpoint] = newEntry;
            return newEntry;
        }
        finally
        {
            gate.Release();
        }
    }

    private Metadata BuildHeaders() => _sharedSecret is not null
        ? new Metadata { { "x-iotgame-secret", _sharedSecret } }
        : new Metadata();

    private CallOptions DeadlinedHeaders() => new(
        headers:  BuildHeaders(),
        deadline: DateTime.UtcNow.AddSeconds(5));

    public async Task SendInputBatch(string endpoint, IEnumerable<InputEventRequest> events)
    {
        try
        {
            var entry   = await EnsureConnectedAsync(endpoint);
            var request = new InputBatchRequest();
            request.Events.AddRange(events);

            foreach (var e in request.Events)
                _logger.LogInformation(
                    "→ Unity | player={PlayerId} input={InputName} event={EventType} value={Value} (device={DeviceId} seq={Seq})",
                    e.PlayerId, e.InputName, e.EventType, e.Value, e.DeviceId, e.SequenceNumber);

            var response = await entry.Client.SendInputBatchAsync(request, DeadlinedHeaders());
            if (!response.Accepted)
                _logger.LogWarning("Unity rejected batch: {Message}", response.Message);
        }
        catch (RpcException ex)
        {
            _logger.LogError("[gRPC] SendInputBatch to {Endpoint} failed: {Status}", endpoint, ex.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[gRPC] SendInputBatch to {Endpoint} unexpected error", endpoint);
        }
    }

    public async Task SendEnvironmentEvent(string endpoint, EnvironmentEvent evt)
    {
        try
        {
            var entry   = await EnsureConnectedAsync(endpoint);
            var request = new EnvironmentEventRequest
            {
                EnvKey    = evt.EnvKey,
                RawValue  = evt.RawValue,
                Timestamp = evt.Timestamp
            };

            _logger.LogInformation("Sending environment event: key={EnvKey} value={Value}", evt.EnvKey, evt.RawValue);
            var response = await entry.Client.SendEnvironmentEventAsync(request, DeadlinedHeaders());

            if (response.Accepted)
                _logger.LogInformation("Unity accepted environment event: {EnvKey}", evt.EnvKey);
            else
                _logger.LogWarning("Unity rejected environment event {EnvKey}: {Message}", evt.EnvKey, response.Message);
        }
        catch (RpcException ex)
        {
            _logger.LogError("[gRPC] SendEnvironmentEvent to {Endpoint} failed: {Status}", endpoint, ex.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[gRPC] SendEnvironmentEvent to {Endpoint} unexpected error", endpoint);
        }
    }

    public async Task SendAdminCommand(string endpoint, string command, string issuedBy,
        Dictionary<string, float>? auxiliary = null)
    {
        var request = new AdminCommandRequest
        {
            Command   = command,
            IssuedBy  = issuedBy,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        if (auxiliary is not null)
            foreach (var kv in auxiliary) request.Auxiliary[kv.Key] = kv.Value;

        await SendAdminCommand(endpoint, request);
    }

    public async Task SendAdminCommand(string endpoint, AdminCommandRequest request)
    {
        try
        {
            var entry = await EnsureConnectedAsync(endpoint);
            _logger.LogInformation("Sending admin command: {Command} by {IssuedBy}", request.Command, request.IssuedBy);
            var response = await entry.Client.SendAdminCommandAsync(request, DeadlinedHeaders());

            if (response.Accepted)
                _logger.LogInformation("Unity accepted admin command: {Command}", request.Command);
            else
                _logger.LogWarning("Unity rejected admin command {Command}: {Message}", request.Command, response.Message);
        }
        catch (RpcException ex)
        {
            _logger.LogError("[gRPC] SendAdminCommand to {Endpoint} failed: {Status}", endpoint, ex.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[gRPC] SendAdminCommand to {Endpoint} unexpected error", endpoint);
        }
    }
}
