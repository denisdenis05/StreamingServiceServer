using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Services.ExternalMusicDownloader;

public interface IExternalMusicDownloader
{
    Task<bool> ScrapeMusicAsync(ReleaseToDownload releaseToDownload);
}