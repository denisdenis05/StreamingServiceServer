namespace StreamingServiceDownloader.Models;

public class AudioMetadata
{
    public string Title { get; set; }
    public string Artist { get; set; }
    public string Album { get; set; }
    public uint? TrackNumber { get; set; }
    public uint? Year { get; set; }
}