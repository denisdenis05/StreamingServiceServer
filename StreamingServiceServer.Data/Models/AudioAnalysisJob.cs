namespace StreamingServiceServer.Data.Models;

public enum AudioAnalysisJobState
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Unavailable = 3,
    Failed = 4
}

public sealed class AudioAnalysisJob
{
    public Guid RecordingId { get; set; }
    public AudioAnalysisJobState State { get; set; }
    public string SourceVersion { get; set; } = string.Empty;
    public Guid Version { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastFailure { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
