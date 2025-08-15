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
        .ToListAsync();

    var releaseIdsToFetch = recordings
        .Where(r => string.IsNullOrEmpty(r.Release.Cover))
        .Select(r => r.Release.Id)
        .Distinct()
        .ToList();

    var coverTasks = releaseIdsToFetch.ToDictionary(
        id => id,
        id => _externalMusicSearchService.GetAlbumCover(id)
    );

    await Task.WhenAll(coverTasks.Values);

    foreach (var recording in recordings)
    {
        if (string.IsNullOrEmpty(recording.Release.Cover))
        {
            recording.Release.Cover = coverTasks[recording.Release.Id].Result;
        }
    }

    return recordings
        .Select(recording => recording.ToResponse())
        .ToList();
}

public async Task<List<ReleaseResponse>> GetAllAlbums()
{
    var releases = await _dbContext.Releases
        .Include(release => release.Artist)
        .ToListAsync();

    var releaseIdsToFetch = releases
        .Where(r => string.IsNullOrEmpty(r.Cover))
        .Select(r => r.Id)
        .Distinct()
        .ToList();

    var coverTasks = releaseIdsToFetch.ToDictionary(
        id => id,
        id => _externalMusicSearchService.GetAlbumCover(id)
    );

    await Task.WhenAll(coverTasks.Values);

    foreach (var release in releases)
    {
        if (string.IsNullOrEmpty(release.Cover))
        {
            release.Cover = coverTasks[release.Id].Result;
        }
    }

    return releases
        .Select(release => release.ToResponse())
        .ToList();
}

public async Task<ICollection<RecordingResponse>> GetRecordingsByAlbumId(Guid id)
{
    var recordings = await _dbContext.Recordings
        .Where(recording => recording.Release.Id == id)
        .Include(recording => recording.Release)
        .Include(recording => recording.Release.Artist)
        .OrderBy(recording => recording.PositionInAlbum)
        .ToListAsync();

    if (!recordings.Any())
    {
        var isQueued = await IsAlreadyQueuedToDownload(id);
        if (!isQueued)
            await QueueToDownloadById(id);

        return Array.Empty<RecordingResponse>();
    }

    if (string.IsNullOrEmpty(recordings.First().Release.Cover))
    {
        var cover = await _externalMusicSearchService.GetAlbumCover(id);
        foreach (var recording in recordings)
        {
            recording.Release.Cover = cover;
        }
    }

    return recordings
        .Select(recording => recording.ToResponse())
        .ToList();
}

public async Task<RecordingResponse> GetRecordingById(Guid id)
{
    var recording = await _dbContext.Recordings
        .Where(r => r.Id == id)
        .Include(r => r.Release)
        .Include(r => r.Release.Artist)
        .FirstOrDefaultAsync();

    if (recording == null)
        return null;

    if (string.IsNullOrEmpty(recording.Release.Cover))
    {
        recording.Release.Cover = await _externalMusicSearchService.GetAlbumCover(recording.Release.Id);
    }

    return recording.ToResponse();
}
    
    public async Task BulkQueueToDownloadByQuery(IEnumerable<AlbumArtistDto> albums)
    {
        foreach (var album in albums)
        {
            await QueueToDownloadByQuery(album.Album, album.Artist);
            await Task.Delay(500);
        }
    }

    public async Task QueueToDownloadByQuery(string albumQuery, string? artistQuery = null)
    {
        try
        {
            var release = await _externalMusicSearchService.SearchAlbumsAsync(albumQuery, artistQuery);
            var releaseToAdd = release.First();

            if (releaseToAdd == null)
                Console.WriteLine("COULD NOT FIND RELEASE");

            if (await IsAlreadyDownloaded(releaseToAdd.Id))
                return;

            if (await IsAlreadyQueuedToDownload(releaseToAdd.Id))
                return;

            await _dbContext.ReleasesToDownload.AddAsync(
                new ReleaseToDownload
                {
                    Id = releaseToAdd.Id,
                    Title = releaseToAdd.Title,
                    Artist = releaseToAdd.Artist.Name
                });
            await _dbContext.SaveChangesAsync();
        }
        catch
        {
            Console.WriteLine("COULD NOT FIND RELEASE");
        }
    }

    public async Task QueueToDownloadById(Guid id)
    {
        if (await IsAlreadyDownloaded(id))
            return;

        if (await IsAlreadyQueuedToDownload(id))
            return;
        
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

        var releaseIds = recordings
            .Select(r => r.Releases.First().Id)
            .Distinct()
            .ToList();

        var coverTasks = releaseIds.ToDictionary(
            id => id,
            id => _externalMusicSearchService.GetAlbumCover(id)
        );

        await Task.WhenAll(coverTasks.Values);

        foreach (var recording in recordings)
        {
            recording.Cover = coverTasks[recording.Releases.First().Id].Result;
        }
        
        return recordings.Select(recording => recording.ToResponse()).ToList();
    }

    public async Task<List<ReleaseResponse>> SearchAlbumsAsync(string query)
    {
        // TODO get Albums from local database and also add relevant external Albums 
        var releases = await _externalMusicSearchService.SearchAlbumsAsync(query);
        // TODO check if recordings are not duplicate with the already saved ones

        foreach (var release in releases)
        {
            release.Cover = await _externalMusicSearchService.GetAlbumCover(release.Id);
        }
        
        return releases.Select(release => release.ToResponse()).ToList();
    }

    public async Task<List<RecordingResponse>> SearchAlbumRecordingsAsync(string query)
    {
        // TODO get Albums from local database and also add relevant external Albums 
        var recordings = await _externalMusicSearchService.SearchAlbumRecordingsAsync(query);
        // TODO check if recordings are not duplicate with the already saved ones

        if (!recordings.Any())
            return new List<RecordingResponse>();

        var cover = await _externalMusicSearchService.GetAlbumCover(recordings.First().Releases.First().Id);
        foreach (var recording in recordings)
        {
            recording.Cover = cover;
        }
        
        return recordings.Select(recording => recording.ToResponse()).ToList();
    }

    public async Task<List<RecordingResponse>> SearchAlbumRecordingsByIdAsync(Guid albumId)
    {
        // TODO get Albums from local database and also add relevant external Albums 
        var recordings = await _externalMusicSearchService.SearchAlbumRecordingsByIdAsync(albumId);
        // TODO check if recordings are not duplicate with the already saved ones
        
        if (!recordings.Any())
            return new List<RecordingResponse>();

        var cover = await _externalMusicSearchService.GetAlbumCover(recordings.First().Releases.First().Id);
        foreach (var recording in recordings)
        {
            recording.Cover = cover;
        }
        
        return recordings.Select(recording => recording.ToResponse()).ToList();
    }

    public async Task SearchAndSaveRecordingAsync(string query)
    {
        var recordings = await _externalMusicSearchService.SearchRecordingsAsync(query);
        var recordingToSave = recordings.First();
        recordingToSave.Cover = await _externalMusicSearchService.GetAlbumCover(recordingToSave.Releases.First().Id);
        
        await _dbContext.Recordings.AddAsync(recordingToSave.ToEntity());
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
        
        var cover = await _externalMusicSearchService.GetAlbumCover(recordings.First().Releases.First().Id);
        foreach (var recording in recordings)
        {
            recording.Cover = cover;
        }
        
        await SaveAlbumRecordings(recordings);
    }

    public async Task SearchAndSaveAlbumRecordingsByIdAsync(Guid albumId)
    {
        var recordings = await _externalMusicSearchService.SearchAlbumRecordingsByIdAsync(albumId);
        if (recordings == null || !recordings.Any())
            return;
        
        var cover = await _externalMusicSearchService.GetAlbumCover(recordings.First().Releases.First().Id);
        foreach (var recording in recordings)
        {
            recording.Cover = cover;
        }

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
    
    public async Task<bool> IsAlreadyDownloaded(Guid id)
    {
        return await _dbContext.Releases.AnyAsync(r => r.Id == id);
    }
    
    
    private async Task<bool> IsAlreadyQueuedToDownload(Guid id)
    {
        var queuedAlbum = await _dbContext.ReleasesToDownload.Where(release => release.Id == id).ToListAsync();

        return queuedAlbum.Any();
    }
}