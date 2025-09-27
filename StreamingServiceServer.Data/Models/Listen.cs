using System.ComponentModel.DataAnnotations.Schema;
using SocialMedia.Data.Models;

namespace StreamingServiceServer.Data.Models;

public class Listen
{
    public Guid ListenId { get; set; }
    
    public Guid UserId { get; set; }
    public User User { get; set; }
    
    public Guid RecordingId { get; set; }
    public Recording Recording { get; set; }
    
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    
    public int TrackDurationSeconds { get; set; }
    public int ServerCalculatedSeconds { get; set; }
    public int ClientReportedSeconds { get; set; } 
    public int ValidatedPlayedSeconds { get; set; } 
    
    public bool NowPlayingReported { get; set; } = false;
    public bool Scrobbled { get; set; } = false;
    public DateTime? ScrobbledAt { get; set; }
    
    public Guid SessionId { get; set; } 
    public bool IsActive { get; set; } = true; 
    
    public DateTime LastProgressUpdate { get; set; }
    
    public string? PauseSeekEvents { get; set; }
    
    public bool HasAnomalies { get; set; }
    public string? AnomalyNotes { get; set; }

    [NotMapped]
    public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
    
    [NotMapped]
    public bool ShouldScrobble => ValidatedPlayedSeconds >= 240 || ValidatedPlayedSeconds >= (TrackDurationSeconds / 2.0);
    
    [NotMapped]
    public List<ProgressCheckpoint> ProgressCheckpoints
    {
        get
        {
            return new List<ProgressCheckpoint>();
        }
        set
        {
        }
    }
}