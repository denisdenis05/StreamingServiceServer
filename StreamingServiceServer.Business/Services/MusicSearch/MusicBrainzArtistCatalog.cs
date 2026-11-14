using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Artists;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public partial class MusicBrainzService : IArtistCatalogProvider
{
    public async Task<ArtistResponse?> GetArtistAsync(Guid artistId, CancellationToken cancellationToken)
    {
        var artist = await GetJsonAsync<ArtistDto>($"{_baseUrl}artist/{artistId}?fmt=json", cancellationToken);
        return artist == null ? null : new ArtistResponse { Id = artist.Id, Name = artist.Name };
    }

    public async Task<ArtistResponse?> ResolveArtistAsync(Guid? recordingId, Guid? albumId, CancellationToken cancellationToken)
    {
        ArtistDto? artist = null;
        if (recordingId.HasValue)
        {
            var recording = await GetJsonAsync<RecordingDto>($"{_baseUrl}recording/{recordingId}?inc=artist-credits&fmt=json", cancellationToken);
            artist = recording?.ArtistCredit?.FirstOrDefault()?.Artist;
        }
        if (artist == null && albumId.HasValue)
        {
            var release = await GetJsonAsync<ReleaseDto>($"{_baseUrl}release/{albumId}?inc=artist-credits&fmt=json", cancellationToken);
            artist = release?.ArtistCredit?.FirstOrDefault()?.Artist;
        }
        return artist == null ? null : new ArtistResponse { Id = artist.Id, Name = artist.Name };
    }

    public async Task<ArtistAlbumPage> GetAlbumsAsync(Guid artistId, int offset, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"arid:{artistId}");
        var response = await GetJsonAsync<MusicBrainzSearchResponse>(
            $"{_baseUrl}release-group?query={query}&limit={ArtistCatalogPolicy.PageSize}&offset={offset}&fmt=json", cancellationToken)
            ?? throw new HttpRequestException("Artist catalog is unavailable.");
        var artist = await GetArtistAsync(artistId, cancellationToken)
            ?? throw new HttpRequestException("Artist details are unavailable.");
        var albums = (response.ReleaseGroups ?? []).Where(group => group.Releases.Any())
            .Select(group =>
            {
                var release = group.Releases.OrderBy(candidate => candidate.Date).ThenBy(candidate => candidate.Id).First();
                return new ReleaseResponse
                {
                    Id = release.Id, Title = group.Title, ArtistName = artist.Name,
                    Type = group.PrimaryType ?? "Album",
                    Cover = $"{_baseCoverUrl}{release.Id}/front",
                    SmallCover = $"{_baseCoverUrl}{release.Id}/front-250",
                    VerySmallCover = $"{_baseCoverUrl}{release.Id}/front-250"
                };
            }).ToArray();
        var nextOffset = offset + ArtistCatalogPolicy.PageSize;
        return new ArtistAlbumPage(artist, albums, nextOffset < response.Count ? nextOffset : null);
    }
}
