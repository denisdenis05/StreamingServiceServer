using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceServer.Business.Services.Images;

public interface IImageService
{
    string GetPreferredCover(string? local, string? optimized, string? original);
    AlbumCoversDto GetAlbumCover(Guid releaseId, Guid? releaseGroupId = null);
    AlbumCoversDto GetAllAlbumCovers(Guid releaseGroupId);
}
