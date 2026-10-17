namespace StreamingServiceServer.Data.Models;
public enum CoverEntityKind { Release = 1, Recording = 2 }
public enum CoverJobPriority { UserChange = 0, Repair = 1, Backfill = 2, Refresh = 3 }
public sealed class CoverAssetJob
{
    public CoverEntityKind EntityKind { get; set; }
    public Guid EntityIdentifier { get; set; }
    public string SourceAddress { get; set; } = string.Empty;
    public string SourceRevision { get; set; } = string.Empty;
    public string InheritedSourceRevision { get; set; } = string.Empty;
    public CoverJobPriority Priority { get; set; }
    public Guid Version { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string LastFailure { get; set; } = string.Empty;
    public string AssetIdentity { get; set; } = string.Empty;
    public string OriginalIdentity { get; set; } = string.Empty;
    public bool Suspended { get; set; }
    public string AssetManifest { get; set; } = string.Empty;
}
