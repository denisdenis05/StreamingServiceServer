using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace StreamingServiceServer.Data.Models;

public class AlbumAnalysisResult
{
    [Key]
    public Guid AlbumId { get; set; }

    [ForeignKey(nameof(AlbumId))]
    public Release? Album { get; set; }

    public int TotalTracks { get; set; }
    public int PresentTracks { get; set; }
    public int MissingTracks { get; set; }

    public string MissingRecordingIdsJson { get; set; } = "[]";

    public DateTime LastAnalyzedAt { get; set; } = DateTime.UtcNow;

    public int MismatchedTracksCount { get; set; }
    public string TrackMatchIssuesJson { get; set; } = "[]";
    public DateTime? LastMismatchCheckedAt { get; set; }
}
