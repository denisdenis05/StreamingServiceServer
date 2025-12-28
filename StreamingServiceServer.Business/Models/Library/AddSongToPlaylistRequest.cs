namespace StreamingServiceServer.Business.Models.Library;

public class AddSongToPlaylistRequest
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
}
