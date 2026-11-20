namespace StreamingServiceServer.Data.Models;

public enum LyricsDownloadState { Pending, Downloaded, Instrumental, NotFound, Skipped, Failed }

public sealed class RecordingLyrics
{
    public Guid RecordingIdentifier { get; set; }
    public LyricsDownloadState State { get; set; }
    public string SourceRevision { get; set; } = string.Empty;
    public long? ProviderIdentifier { get; set; }
    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }
    public string? Lyricsfile { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string LastFailure { get; set; } = string.Empty;
}

public sealed class LyricsQueueState
{
    public string Provider { get; set; } = string.Empty;
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset NextRequestAt { get; set; }
}
