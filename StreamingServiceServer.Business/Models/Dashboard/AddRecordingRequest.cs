namespace StreamingServiceServer.Business.Models.Dashboard;

public class AddRecordingRequest
{
    public string Title { get; set; } = string.Empty;
    public int PositionInAlbum { get; set; }
    public int? LengthMs { get; set; }
}
