using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceServer.Business.Helpers;

public static class MusicLocationHelper
{
    public static string GetRelativeFolderPathForRecording(RecordingResponse recording)
    {
        var artistFolder = recording.ArtistName.Replace(" ", "%").ToUpper();
        var albumFolder = recording.ReleaseTitle.Replace(" ", "%").ToUpper();
        
        return $"{artistFolder}/{albumFolder}/";
    }
}