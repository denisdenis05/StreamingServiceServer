using System.Net.Http.Json;
using Npgsql;
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

        return await SearchAlbumRecordingsByIdAsync(id);
    }

    var albumCovers = new AlbumCoversDto
    {
        Cover = recordings.First().Release.Cover,
        SmallCover = recordings.First().Release.SmallCover,
        VerySmallCover = recordings.First().Release.VerySmallCover
    };
    
    foreach (var recording in recordings)
    {
        if (string.IsNullOrEmpty(recording.Release.Cover))
        {
            recording.Release.Cover = albumCovers.Cover;
            recording.Release.SmallCover = albumCovers.SmallCover;
            recording.Release.VerySmallCover = albumCovers.VerySmallCover;
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

    public async Task<RecordingResponse> SearchRecordingByIdAsync(Guid id)
    {
        var recording = await _externalMusicSearchService.SearchRecordingByIdAsync(id);
        
        return recording.ToResponse();
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
        
        var releases = recordings.First().Releases;
        var release = releases.First();
        var cover = new AlbumCoversDto();
        
        if (release.ReleaseGroup != null)
            cover = await _externalMusicSearchService.GetAllAlbumCovers(release.ReleaseGroup.Id);
        else
            cover = await _externalMusicSearchService.GetAlbumCover(release.Id);
        
        release.Cover = cover;
        foreach (var recording in recordings)
        {
            recording.Cover = cover;
        }

        await SaveAlbumRecordings(recordings);
    }

    private async Task SaveAlbumRecordings(ICollection<RecordingDto> recordings)
{
    const int maxRetries = 3;
    int retryCount = 0;

    while (retryCount < maxRetries)
    {
        try
        {
            await SaveAlbumRecordingsInternal(recordings);
            return; 
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
        {
            retryCount++;
            
            _dbContext.ChangeTracker.Clear();
            
            if (retryCount >= maxRetries)
            {
                throw new InvalidOperationException($"Failed to save album recordings after {maxRetries} attempts due to concurrent modifications.", ex);
            }
            
            await Task.Delay(100 * retryCount);
        }
    }
}

private async Task SaveAlbumRecordingsInternal(ICollection<RecordingDto> recordings)
{
    using var transaction = await _dbContext.Database.BeginTransactionAsync();
    
    try
    {
        await UpsertArtists(recordings);
        
        var release = await UpsertRelease(recordings);
        
        await UpsertRecordings(recordings, release);
        
        RemoveDuplicatesFromChangeTracker();
        
        await _dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }
}

private async Task UpsertArtists(ICollection<RecordingDto> recordings)
{
    var allArtists = new List<Artist>();

    var releaseArtist = recordings.First().Releases.First().Artist?.ToEntity();
    if (releaseArtist != null)
        allArtists.Add(releaseArtist);

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

    if (!distinctArtists.Any())
        return;

    var artistIds = distinctArtists.Select(a => a.Id).ToList();
    
    var existingArtists = await _dbContext.Artists
        .Where(a => artistIds.Contains(a.Id))
        .ToDictionaryAsync(a => a.Id, a => a);

    foreach (var artist in distinctArtists)
    {
        var trackedArtist = _dbContext.Artists.Local.FirstOrDefault(a => a.Id == artist.Id);
        
        if (trackedArtist != null)
        {
            continue;
        }
        
        if (existingArtists.TryGetValue(artist.Id, out var existingArtist))
        {
            _dbContext.Artists.Attach(existingArtist);
        }
        else
        {
            _dbContext.Artists.Add(artist);
        }
    }
}

private async Task<Release> UpsertRelease(ICollection<RecordingDto> recordings)
{
    var releaseDto = recordings.First().Releases.First();
    var release = releaseDto.ToEntity();

    var trackedRelease = _dbContext.Releases.Local.FirstOrDefault(r => r.Id == release.Id);
    if (trackedRelease != null)
    {
        return trackedRelease;
    }

    var existingRelease = await _dbContext.Releases.FirstOrDefaultAsync(r => r.Id == release.Id);
    
    if (existingRelease != null)
    {
        _dbContext.Entry(existingRelease).CurrentValues.SetValues(release);
        
        if (release.Artist != null)
        {
            var artistInContext = _dbContext.Artists.Local.FirstOrDefault(a => a.Id == release.Artist.Id) 
                                ?? await _dbContext.Artists.FirstOrDefaultAsync(a => a.Id == release.Artist.Id);
            
            if (artistInContext != null)
            {
                existingRelease.Artist = artistInContext;
            }
        }
        
        return existingRelease;
    }
    else
    {
        if (release.Artist != null)
        {
            var artistInContext = _dbContext.Artists.Local.FirstOrDefault(a => a.Id == release.Artist.Id);
            if (artistInContext != null)
            {
                release.Artist = artistInContext;
            }
        }
        
        _dbContext.Releases.Add(release);
        return release;
    }
}

private async Task UpsertRecordings(ICollection<RecordingDto> recordings, Release release)
{
    var recordingEntities = recordings
        .Select(r => r.ToEntity())
        .GroupBy(r => r.Id)
        .Select(g => g.First())
        .ToList();

    foreach (var recording in recordingEntities)
    {
        recording.Release = release;

        var trackedRecording = _dbContext.Recordings.Local.FirstOrDefault(r => r.Id == recording.Id);
        if (trackedRecording != null)
        {
            continue;
        }

        var existingRecording = await _dbContext.Recordings.FirstOrDefaultAsync(r => r.Id == recording.Id);
        
        if (existingRecording != null)
        {
            _dbContext.Entry(existingRecording).CurrentValues.SetValues(recording);
            existingRecording.Release = release;
        }
        else
        {
            _dbContext.Recordings.Add(recording);
        }
    }

    await UpsertRecordingArtistCredits(recordings);
}

private async Task UpsertRecordingArtistCredits(ICollection<RecordingDto> recordings)
{
    var allCredits = recordings
        .SelectMany(r => r.ArtistCredit.Select(ac => new
        {
            RecordingId = r.Id,
            Credit = ac
        }))
        .ToList();

    foreach (var creditInfo in allCredits)
    {
        if (creditInfo.Credit.Artist == null) continue;

        var credit = new RecordingArtistCredit
        {
            Id = Guid.NewGuid(), 
            RecordingId = creditInfo.RecordingId,
            ArtistId = creditInfo.Credit.Artist.Id,
            Name = creditInfo.Credit.Name
        };

        var existingCredit = await _dbContext.Set<RecordingArtistCredit>()
            .FirstOrDefaultAsync(rac => rac.RecordingId == credit.RecordingId && 
                                       rac.ArtistId == credit.ArtistId);

        if (existingCredit == null)
        {
            var artistInContext = _dbContext.Artists.Local.FirstOrDefault(a => a.Id == credit.ArtistId);
            if (artistInContext != null)
            {
                credit.Artist = artistInContext;
            }

            _dbContext.Set<RecordingArtistCredit>().Add(credit);
        }
    }
}

private void RemoveDuplicatesFromChangeTracker()
{
    var duplicateArtists = _dbContext.ChangeTracker.Entries<Artist>()
        .Where(e => e.State == EntityState.Added)
        .GroupBy(e => e.Entity.Id)
        .Where(g => g.Count() > 1);

    foreach (var duplicateGroup in duplicateArtists)
    {
        var duplicates = duplicateGroup.Skip(1);
        foreach (var duplicate in duplicates)
        {
            duplicate.State = EntityState.Detached;
        }
    }

    var duplicateReleases = _dbContext.ChangeTracker.Entries<Release>()
        .Where(e => e.State == EntityState.Added)
        .GroupBy(e => e.Entity.Id)
        .Where(g => g.Count() > 1);

    foreach (var duplicateGroup in duplicateReleases)
    {
        var duplicates = duplicateGroup.Skip(1);
        foreach (var duplicate in duplicates)
        {
            duplicate.State = EntityState.Detached;
        }
    }

    var duplicateRecordings = _dbContext.ChangeTracker.Entries<Recording>()
        .Where(e => e.State == EntityState.Added)
        .GroupBy(e => e.Entity.Id)
        .Where(g => g.Count() > 1);

    foreach (var duplicateGroup in duplicateRecordings)
    {
        var duplicates = duplicateGroup.Skip(1);
        foreach (var duplicate in duplicates)
        {
            duplicate.State = EntityState.Detached;
        }
    }
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
    
    public async Task RefreshAllAlbumCovers()
    {
        var allReleases = await _dbContext.Releases
            .Include(r => r.Recordings)
            .ToListAsync();

        foreach (var release in allReleases)
        {
            AlbumCoversDto? albumCovers = null;

            var externalReleaseDtos = await _externalMusicSearchService.GetReleasesFromId(release.Id);
            var externalReleaseDto = externalReleaseDtos.FirstOrDefault();

            if (externalReleaseDto?.ReleaseGroup?.Id != null && externalReleaseDto.ReleaseGroup.Id != Guid.Empty)
            {
                var releaseGroupId = externalReleaseDto.ReleaseGroup.Id;
                albumCovers = await _externalMusicSearchService.GetAllAlbumCovers(releaseGroupId);
            }
            else
            {
                var singleCoverUrl = await _externalMusicSearchService.GetAlbumCover(release.Id);
                albumCovers = new AlbumCoversDto
                {
                    Cover = singleCoverUrl.Cover,
                    SmallCover = singleCoverUrl.SmallCover,
                    VerySmallCover = singleCoverUrl.VerySmallCover
                };
            }

            if (albumCovers != null)
            {
                release.Cover = albumCovers.Cover;
                release.SmallCover = albumCovers.SmallCover;
                release.VerySmallCover = albumCovers.VerySmallCover;

                // Apply covers to each Recording within this Release
                foreach (var recording in release.Recordings)
                {
                    recording.Cover = albumCovers.Cover;
                    recording.SmallCover = albumCovers.SmallCover;
                    recording.VerySmallCover = albumCovers.VerySmallCover;
                }
            }
        }
        await _dbContext.SaveChangesAsync();
    }
}