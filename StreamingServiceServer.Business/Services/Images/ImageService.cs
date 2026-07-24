using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceServer.Business.Services.Images;

public class ImageService : IImageService
{
    private readonly string _baseCoverUrl;

    public ImageService(IConfiguration configuration)
    {
        _baseCoverUrl = configuration["Music:External:AlbumCover:BaseUrl"];
    }

    public AlbumCoversDto GetAlbumCover(Guid releaseId, Guid? releaseGroupId = null)
    {
        var albumCovers = new AlbumCoversDto();
        var targetUrlPrefix = releaseGroupId.HasValue
            ? $"{_baseCoverUrl}release-group/{releaseGroupId.Value}"
            : $"{_baseCoverUrl}release/{releaseId}";

        albumCovers.Cover = $"{targetUrlPrefix}/front";
        albumCovers.SmallCover = $"{targetUrlPrefix}/front-500";
        albumCovers.VerySmallCover = $"{targetUrlPrefix}/front-250";

        return albumCovers;
    }

    public AlbumCoversDto GetAllAlbumCovers(Guid releaseGroupId)
    {
        var albumCovers = new AlbumCoversDto();

        var frontCoverUrl = $"{_baseCoverUrl}release-group/{releaseGroupId}/front";
        albumCovers.Cover = frontCoverUrl;

        var front500CoverUrl = $"{_baseCoverUrl}release-group/{releaseGroupId}/front-500";
        albumCovers.SmallCover = front500CoverUrl;

        var front250CoverUrl = $"{_baseCoverUrl}release-group/{releaseGroupId}/front-250";
        albumCovers.VerySmallCover = front250CoverUrl;

        return albumCovers;
    }
}
