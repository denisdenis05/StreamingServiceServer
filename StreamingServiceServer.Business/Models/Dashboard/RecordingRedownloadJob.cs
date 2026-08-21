namespace StreamingServiceServer.Business.Models.Dashboard;

public class RecordingRedownloadJob
{
    public string State { get; set; } = "pending";
    public string? Message { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
