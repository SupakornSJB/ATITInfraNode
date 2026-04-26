namespace ATITInfraNode.Services;

public class ZoneResolverService
{
    private int  _currentZone    = 3;
    private int  _candidateZone  = 3;
    private long _candidateSince = 0;
    private const int HysteresisMs = 500;

    // Returns the new zone ID only when a confirmed zone transition occurs; null otherwise.
    public int? Resolve(float distanceCm)
    {
        int raw = distanceCm switch {
            < 20  => 1,
            < 60  => 2,
            < 120 => 3,
            _     => 4
        };

        if (raw == _currentZone) { _candidateZone = raw; return null; }

        if (raw != _candidateZone)
        {
            _candidateZone  = raw;
            _candidateSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return null;
        }

        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _candidateSince;
        if (elapsed < HysteresisMs) return null;

        _currentZone = raw;
        return _currentZone;
    }
}