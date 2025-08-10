using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceDownloader.Models;

public class MusicFileMatch
{
    public string FileName { get; set; }
    public string FullPath { get; set; }
    public RecordingResponse Recording { get; set; }
    public int Score { get; set; }
}