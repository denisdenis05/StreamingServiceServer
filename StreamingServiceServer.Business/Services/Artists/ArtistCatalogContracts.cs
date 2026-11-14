using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceServer.Business.Services.Artists;

public static class ArtistCatalogPolicy
{
    public const int PageSize = 24;
    public const int FirstOffset = 0;
    public const int MaximumOffset = 100000;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
}

public sealed record ArtistAlbumPage(ArtistResponse Artist, IReadOnlyList<ReleaseResponse> Albums, int? NextOffset);

public interface IArtistCatalogProvider
{
    Task<ArtistResponse?> GetArtistAsync(Guid artistId, CancellationToken cancellationToken);
    Task<ArtistResponse?> ResolveArtistAsync(Guid? recordingId, Guid? albumId, CancellationToken cancellationToken);
    Task<ArtistAlbumPage> GetAlbumsAsync(Guid artistId, int offset, CancellationToken cancellationToken);
}
