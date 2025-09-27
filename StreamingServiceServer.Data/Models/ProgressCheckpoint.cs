namespace StreamingServiceServer.Data.Models;

public class ProgressCheckpoint
{
    public DateTime Timestamp { get; set; }
    public int ReportedSeconds { get; set; }
    public int ExpectedMinimum { get; set; }
    public int ExpectedMaximum { get; set; }
    public bool IsValid { get; set; }
}