namespace IoTGame.Core.Models;

public class DeviceSecurityState
{
    public string          DeviceId            { get; init; } = string.Empty;
    public bool            IsQuarantined       { get; set; }
    public int             AlertCount          { get; set; }
    public DateTimeOffset  LastSeen            { get; set; }
    public DateTimeOffset? QuarantinedAt       { get; set; }
    public double          CurrentRate         { get; set; }  // events/sec (1-second rolling window)
    public long?           LastSequenceNumber  { get; set; }
}
