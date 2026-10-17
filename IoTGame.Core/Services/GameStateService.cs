namespace IoTGame.Core.Services;

public enum RoundStatus { Idle, Active, Ended }

public class GameStateService
{
    private RoundStatus _status = RoundStatus.Idle;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endedAt;
    private readonly object _lock = new();

    public RoundStatus Status       => _status;
    public DateTimeOffset? StartedAt => _startedAt;
    public DateTimeOffset? EndedAt   => _endedAt;

    public void MarkStarted()
    {
        lock (_lock) { _status = RoundStatus.Active; _startedAt = DateTimeOffset.UtcNow; _endedAt = null; }
    }

    public void MarkEnded()
    {
        lock (_lock) { _status = RoundStatus.Ended; _endedAt = DateTimeOffset.UtcNow; }
    }

    public void Reset()
    {
        lock (_lock) { _status = RoundStatus.Idle; _startedAt = null; _endedAt = null; }
    }
}
