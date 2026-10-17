namespace StreamingServiceServer.Data.Models;
public enum CoverEntityKind { Release = 1, Recording = 2 }
public sealed class CoverAssetJob
{
    public CoverEntityKind EntityKind { get; set; }
    public Guid EntityIdentifier { get; set; }
    public string SourceAddress { get; set; } = string.Empty;
    public Guid Version { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string LastFailure { get; set; } = string.Empty;
    public string AssetManifest { get; set; } = string.Empty;
}
