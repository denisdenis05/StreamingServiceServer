using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Models.MusicSearch;
namespace StreamingServiceServer.Business.Services.Images;

public class ImageService : IImageService
{
    private readonly Uri _archiveRoot;
    private readonly string _domain;
    private readonly string _imagePath;
    public ImageService(IConfiguration configuration)
    {
        _archiveRoot = CoverPolicy.ArchiveRoot(configuration["Music:External:AlbumCover:BaseUrl"]);
        _domain = configuration["Domain"] ?? string.Empty;
        _imagePath = configuration["Cover:DomainExtension"] ?? "/images/";
    }
    public string GetPreferredCover(string? local, string? optimized, string? original)
    {
        if (!string.IsNullOrWhiteSpace(local)) local = CoverPolicy.LocalAddress(local, _domain, _imagePath);
        return CoverPolicy.Select(local, optimized, original);
    }
    public AlbumCoversDto GetAlbumCover(Guid releaseId, Guid? releaseGroupId = null) =>
        CoverPolicy.Build(_archiveRoot, releaseGroupId ?? releaseId, releaseGroupId.HasValue);
    public AlbumCoversDto GetAllAlbumCovers(Guid releaseGroupId) => CoverPolicy.Build(_archiveRoot, releaseGroupId, true);
}
