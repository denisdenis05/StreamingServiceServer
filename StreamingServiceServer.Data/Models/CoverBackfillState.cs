namespace StreamingServiceServer.Data.Models;

public sealed class CoverBackfillState
{
    public CoverEntityKind EntityKind { get; set; }
    public DateTime? PositionAddedAt { get; set; }
    public Guid? PositionIdentifier { get; set; }
    public DateTimeOffset NextRestartAt { get; set; }
    public Guid Version { get; set; }
}
