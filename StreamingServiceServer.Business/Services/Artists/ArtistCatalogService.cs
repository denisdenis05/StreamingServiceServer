using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Data;

namespace StreamingServiceServer.Business.Services.Artists;

public sealed class ArtistCatalogService(StreamingDbContext database, IArtistCatalogProvider provider,
    IImageService images, IMemoryCache cache)
{
    public async Task<ArtistResponse?> ResolveAsync(Guid? recordingId, Guid? albumId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recordingId.HasValue)
        {
            var artist = await database.Recordings.AsNoTracking().Where(recording => recording.Id == recordingId)
                .Select(recording => new ArtistResponse { Id = recording.Release.Artist.Id, Name = recording.Release.Artist.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (artist != null) return artist;
        }
        if (albumId.HasValue)
        {
            var artist = await database.Releases.AsNoTracking().Where(release => release.Id == albumId)
                .Select(release => new ArtistResponse { Id = release.Artist.Id, Name = release.Artist.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (artist != null) return artist;
        }
        return await provider.ResolveArtistAsync(recordingId, albumId, cancellationToken);
    }

    public async Task<ArtistAlbumPage?> GetAsync(Guid artistId, int offset, bool catalog, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog)
        {
            var key = (nameof(ArtistCatalogService), artistId, offset);
            if (cache.TryGetValue<ArtistAlbumPage>(key, out var cached)) return cached;
            var page = await provider.GetAlbumsAsync(artistId, offset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            cache.Set(key, page, ArtistCatalogPolicy.CacheLifetime);
            return page;
        }
        var artist = await database.Artists.AsNoTracking().Where(candidate => candidate.Id == artistId)
            .Select(candidate => new ArtistResponse { Id = candidate.Id, Name = candidate.Name })
            .FirstOrDefaultAsync(cancellationToken) ?? await provider.GetArtistAsync(artistId, cancellationToken);
        if (artist == null) return null;
        var albums = await database.Releases.AsNoTracking().Include(release => release.Artist)
            .Where(release => release.Artist.Id == artistId)
            .OrderBy(release => release.Title).ThenBy(release => release.Id)
            .Skip(offset).Take(ArtistCatalogPolicy.PageSize + 1).ToListAsync(cancellationToken);
        return new ArtistAlbumPage(artist, albums.Take(ArtistCatalogPolicy.PageSize).Select(release => release.ToResponse(images)).ToArray(),
            albums.Count > ArtistCatalogPolicy.PageSize ? offset + ArtistCatalogPolicy.PageSize : null);
    }
}
