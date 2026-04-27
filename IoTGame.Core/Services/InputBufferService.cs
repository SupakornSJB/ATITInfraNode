using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using GameController.V2;
using IoTGame.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoTGame.Core.Services;

public class InputBufferService : IHostedService, IAsyncDisposable
{
    private readonly UnityRelayService _unityRelay;
    private readonly ILogger<InputBufferService> _logger;
    private readonly int _bufferWindowMs;
    private readonly int _maxEventsPerSecond;

    // Key: endpoint → (lock, buffer)
    private readonly ConcurrentDictionary<string, (SemaphoreSlim Lock, List<(string Endpoint, InputEventRequest Req)>)>
        _endpointBuffers = new();
    private readonly SemaphoreSlim _globalLock = new(1, 1);
    private readonly List<(string Endpoint, InputEventRequest Req)> _buffer = new();
    private readonly ConcurrentDictionary<string, RateLimiter> _rateLimiters = new();

    
    private PeriodicTimer? _timer;
    private Task? _flushTask;
    private CancellationTokenSource? _cts;

    public InputBufferService(
        UnityRelayService unityRelay,
        IoTGameOptions options,
        ILogger<InputBufferService> logger)
    {
        _unityRelay = unityRelay;
        _logger = logger;
        _bufferWindowMs = options.BufferWindowMs;
        _maxEventsPerSecond = options.MaxEventsPerSecond;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_bufferWindowMs > 0)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_bufferWindowMs));
            _flushTask = FlushLoopAsync(_cts.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_flushTask is not null)
                await _flushTask.ConfigureAwait(false);
        }
    }

    public async Task EnqueueAsync(string endpoint, InputEvent evt)
    {
        var key = $"{evt.DeviceId}:{evt.SlotId}";
        var limiter = _rateLimiters.GetOrAdd(key, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit          = _maxEventsPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod     = _maxEventsPerSecond,
            AutoReplenishment   = true,
            QueueLimit          = 0,
            QueueProcessingOrder = QueueProcessingOrder.NewestFirst
        }));

        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            _logger.LogWarning("Rate limit exceeded for {Key}, dropping event", key);
            return;
        }

        _logger.LogDebug(
            "Enqueued {DeviceId}/{SlotId} → player={PlayerId} input={InputName} event={EventType} value={Value} seq={Seq}",
            evt.DeviceId, evt.SlotId, evt.PlayerId, evt.InputName, evt.EventType, evt.Value, evt.SequenceNumber);

        var request = new InputEventRequest
        {
            PlayerId       = evt.PlayerId,
            DeviceId       = evt.DeviceId,
            InputName      = evt.InputName ?? "",
            InputType      = evt.InputType,
            EventType      = evt.EventType,
            Value          = evt.Value,
            Timestamp      = evt.Timestamp,
            SequenceNumber = evt.SequenceNumber
        };

        if (_bufferWindowMs == 0)
        {
            await _unityRelay.SendInputBatch(endpoint, [request]);
            return;
        }

        await _globalLock.WaitAsync();
        try { _buffer.Add((endpoint, request)); }
        finally { _globalLock.Release(); }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct))
            {
                await _globalLock.WaitAsync(ct);
                List<(string Endpoint, InputEventRequest Req)> snapshot;
                try
                {
                    if (_buffer.Count == 0) continue;
                    snapshot = new List<(string, InputEventRequest)>(_buffer);
                    _buffer.Clear();
                }
                finally { _globalLock.Release(); }

                // Group by endpoint and flush each separately
                foreach (var group in snapshot.GroupBy(x => x.Endpoint))
                    await _unityRelay.SendInputBatch(group.Key, group.Select(x => x.Req));
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        _cts?.Dispose();
        _globalLock.Dispose();
        foreach (var limiter in _rateLimiters.Values)
            await limiter.DisposeAsync();
    }
}
