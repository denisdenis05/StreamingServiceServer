using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceServer.PreviewDownloader;

public interface IPreviewDownloaderService
{
    Task<string> DownloadPreview(RecordingResponse recording);
}
