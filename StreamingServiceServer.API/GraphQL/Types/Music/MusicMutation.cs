using StreamingServiceServer.API.GraphQL.Types.Music.MusicTypes;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.MusicSearch;

namespace StreamingServiceServer.API.GraphQL.Types.Music;

public class MusicMutation
{
    private readonly IMetadataService _metadataService;

    public MusicMutation(IMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    public async Task<bool> SaveArtist(string query)
    {
        await _metadataService.SearchAndSaveArtistAsync(query);
        return true;
    }

    public async Task<bool> SaveRecording(string query)
    {
        await _metadataService.SearchAndSaveRecordingAsync(query);
        return true;
    }

    public async Task<bool> SaveRelease(string query)
    {
        await _metadataService.SearchAndSaveAlbumRecordingsAsync(query);
        return true;
    }

    public async Task<bool> QueueDownload(string album, string? artist = null)
    {
        await _metadataService.QueueToDownloadByQuery(album, artist);
        return true;
    }

    public async Task<bool> BulkQueueDownloads(List<AlbumArtistInput> albums)
    {
        var dtos = albums.Select(a => new AlbumArtistDto { Album = a.Album, Artist = a.Artist });
        await _metadataService.BulkQueueToDownloadByQuery(dtos);
        return true;
    }
}