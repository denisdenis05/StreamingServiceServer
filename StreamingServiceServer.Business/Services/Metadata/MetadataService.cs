using System.Net.Http.Json;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;
using Microsoft.EntityFrameworkCore;



public class MetadataService : IMetadataService
{
    private readonly StreamingDbContext _dbContext;
    private readonly IExternalMusicSearchService _externalMusicSearchService;

    public MetadataService(StreamingDbContext dbContext, IExternalMusicSearchService externalMusicSearchService)
    {
        _dbContext = dbContext;
        _externalMusicSearchService = externalMusicSearchService;
    }


    public async Task<List<string>> GetAllArtistNames()
    {
        var artistNames = await _dbContext.Artists.Select(artist => artist.Name).ToListAsync();

        return artistNames;
    }

    public async Task<List<RecordingResponse>> GetAllRecordings()
    {
        var recordings = await _dbContext.Recordings
            .Include(recording => recording.ArtistCredit)
            .Include(recording => recording.Release)
            .Select(recording => recording.ToResponse())
            .ToListAsync();

        return recordings;
    }

    public async Task<List<ReleaseResponse>> GetAllAlbums()
    {
        var releases = await _dbContext.Releases
            .Include(release => release.Artist)
            .Select(release => release.ToResponse())
            .ToListAsync();

        return releases;
    }

    public async Task<ICollection<RecordingResponse>> GetRecordingsByAlbumId(Guid id)
    {
        var recordings = await _dbContext.Recordings
            .Where(recording => recording.Release.Id == id)
            .Include(recording => recording.Release)
            .Include(recording => recording.Release.Artist)
            .OrderBy(recording => recording.PositionInAlbum)
            .Select(recording => recording.ToResponse())
            .ToListAsync();

        if (!recordings.Any())
        {
            var isQueued = await IsAlreadyQueuedToDownload(id);
            if (!isQueued)
                await QueueToDownload(id);
        }

        return recordings;
    }

    public async Task<RecordingResponse> GetRecordingById(Guid id)
    {
        var recordings = await _dbContext.Recordings
            .Where(recording => recording.Id == id)
            .Include(recording => recording.Release)
            .Include(recording => recording.Release.Artist)
            .Select(recording => recording.ToResponse())
            .FirstOrDefaultAsync();

        return recordings;
    }

    public async Task<bool> IsAlreadyQueuedToDownload(Guid id)
    {
        var queuedAlbum = await _dbContext.ReleasesToDownload.Where(release => release.Id == id).ToListAsync();

        return queuedAlbum.Any();
    }

    public async Task QueueToDownload(Guid id)
    {
        var release = await _externalMusicSearchService.GetAlbumDetails(id);

        await _dbContext.ReleasesToDownload.AddAsync(
            new ReleaseToDownload
            {
                Id = id,
                Title = release.Title,
                Artist = release.ArtistName
            });
        await _dbContext.SaveChangesAsync();
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query)
    {
        // TODO get artists from local database and also add relevant external artists 
        var artists = await _externalMusicSearchService.SearchArtistsAsync(query);
        // TODO check if artists are not duplicate with the already saved ones

        return artists.Select(artist => artist.ToEntity()).ToList();
    }

    public async Task<List<RecordingResponse>> SearchRecordingsAsync(string query)
    {
        // TODO get Recordings from local database and also add relevant external Recordings 
        var recordings = await _externalMusicSearchService.SearchRecordingsAsync(query);
        // TODO check if recordings are not duplicate with the already saved ones

        return recordings.Select(recording => recording.ToResponse()).ToList();
    }

    public async Task<List<ReleaseResponse>> SearchAlbumsAsync(string query)
    {
        // TODO get Albums from local database and also add relevant external Albums 
        var releases = await _externalMusicSearchService.SearchAlbumsAsync(query);
        // TODO check if recordings are not duplicate with the already saved ones
        return releases.Select(release => release.ToResponse()).ToList();
    }

    public async Task<List<RecordingResponse>> SearchAlbumRecordingsAsync(string query)
    {
        // TODO get Albums from local database and also add relevant external Albums 
        var recordings = await _externalMusicSearchService.SearchAlbumRecordingsAsync(query);
        // TODO check if recordings are not duplicate with the already saved ones
        return recordings.Select(recording => recording.ToResponse()).ToList();
    }

    public async Task<List<RecordingResponse>> SearchAlbumRecordingsByIdAsync(Guid albumId)
    {
        // TODO get Albums from local database and also add relevant external Albums 
        var recordings = await _externalMusicSearchService.SearchAlbumRecordingsByIdAsync(albumId);
        // TODO check if recordings are not duplicate with the already saved ones
        return recordings.Select(recording => recording.ToResponse()).ToList();
    }

    public async Task SearchAndSaveRecordingAsync(string query)
    {
        var recordings = await _externalMusicSearchService.SearchRecordingsAsync(query);

        await _dbContext.Recordings.AddAsync(recordings.First().ToEntity());
        await _dbContext.SaveChangesAsync();
    }

    public async Task SearchAndSaveArtistAsync(string query)
    {
        var artists = await _externalMusicSearchService.SearchArtistsAsync(query);

        await _dbContext.Artists.AddAsync(artists.First().ToEntity());
        await _dbContext.SaveChangesAsync();
    }

    public async Task SearchAndSaveAlbumRecordingsAsync(string query)
    {
        var recordings = await _externalMusicSearchService.SearchAlbumRecordingsAsync(query);
        await SaveAlbumRecordings(recordings);
    }

    public async Task SearchAndSaveAlbumRecordingsByIdAsync(Guid albumId)
    {
        var recordings = await _externalMusicSearchService.SearchAlbumRecordingsByIdAsync(albumId);
        if (recordings == null || !recordings.Any())
            return;

        await SaveAlbumRecordings(recordings);
    }

    private async Task SaveAlbumRecordings(ICollection<RecordingDto> recordings)
    {
        var release = recordings.First().Releases.First().ToEntity();

        var allArtists = new List<Artist>();

        if (release.Artist != null)
            allArtists.Add(release.Artist);

        var recordingArtists = recordings
            .SelectMany(r => r.ArtistCredit)
            .Where(ac => ac.Artist != null)
            .Select(ac => ac.Artist!.ToEntity())
            .ToList();

        allArtists.AddRange(recordingArtists);

        var distinctArtists = allArtists
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .ToList();

        var existingArtistIds = await _dbContext.Artists
            .Where(a => distinctArtists.Select(ua => ua.Id).Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync();

        foreach (var artist in distinctArtists)
        {
            bool tracked = _dbContext.Artists.Local.Any(a => a.Id == artist.Id);
            bool exists = existingArtistIds.Contains(artist.Id);

            if (!tracked && !exists)
            {
                await _dbContext.Artists.AddAsync(artist);
            }
            else if (exists && !tracked)
            {
                _dbContext.Artists.Attach(artist);
            }
        }

        await _dbContext.Releases.AddAsync(release);

        var recordingEntities = recordings.Select(recording =>
        {
            var recEntity = recording.ToEntity();
            recEntity.Release = release;
            return recEntity;
        }).ToList();

        await _dbContext.Recordings.AddRangeAsync(recordingEntities);

        await _dbContext.SaveChangesAsync();
    }
}