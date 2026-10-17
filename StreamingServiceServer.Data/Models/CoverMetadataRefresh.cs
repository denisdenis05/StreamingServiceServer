namespace StreamingServiceServer.Data.Models;

public sealed class CoverMetadataRefresh
{
    public const string OperationKey = "album-covers";
    public string Id { get; set; } = OperationKey;
    public Guid? Cursor { get; set; }
    public bool Pending { get; set; }
    public long CompletedReleases { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid Version { get; set; }
    public string LastFailure { get; set; } = string.Empty;
}
