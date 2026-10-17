using System.Net.Http.Json;
using Npgsql;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Services.MusicSearch;
using Microsoft.EntityFrameworkCore;

public class MetadataService : IMetadataService
{
    private readonly StreamingDbContext _dbContext;
    private readonly IExternalMusicSearchService _externalMusicSearchService;
    private readonly IImageService _imageService;
    private readonly ILibraryService _libraryService;

    public MetadataService(StreamingDbContext dbContext, IExternalMusicSearchService externalMusicSearchService, IImageService imageService, ILibraryService libraryService)
    {
        _dbContext = dbContext;
        _externalMusicSearchService = externalMusicSearchService;
        _imageService = imageService;
        _libraryService = libraryService;
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

        return recordings
            .Select(recording => recording.ToResponse(_imageService))
            .ToList();
    }

    public async Task<List<RecordingResponse>> GetSuggestedRecordings(int count)
    {
        var recordings = await _dbContext.Recordings
            .Include(r => r.ArtistCredit)
            .Include(r => r.Release)
            .ThenInclude(rel => rel.Artist)
            .Where(r => r.Release != null)
            .OrderBy(_ => Guid.NewGuid())
            .Take(count)
            .ToListAsync();

        return recordings
            .Select(r => r.ToResponse(_imageService))
            .ToList();
    }
    public async Task<List<ReleaseResponse>> GetAllAlbums()
    {
        var releases = await _dbContext.Releases
            .Include(release => release.Artist)
            .ToListAsync();

        return releases
            .Select(release => release.ToResponse(_imageService))
            .ToList();
    }

    public async Task<ICollection<RecordingResponse>> GetRecordingsByAlbumId(Guid id, Guid userId)
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

        var likedRecordingIds = await _libraryService.GetLikedRecordingIdsAsync(userId, CancellationToken.None);

        return recordings
            .Select(recording => recording.ToResponse(_imageService, likedRecordingIds.Contains(recording.Id)))
            .ToList();
    }

    public async Task<RecordingResponse> GetRecordingById(Guid id, Guid userId)
    {
        var recording = await _dbContext.Recordings
            .Where(r => r.Id == id)
            .Include(r => r.Release)
            .Include(r => r.Release.Artist)
            .FirstOrDefaultAsync();

        if (recording == null)
            return null;

        var likedRecordingIds = await _libraryService.GetLikedRecordingIdsAsync(userId, CancellationToken.None);

        return recording.ToResponse(_imageService, likedRecordingIds.Contains(recording.Id));
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

    public async Task<RecordingResponse> SearchRecordingByIdAsync(Guid id)
    {
        var recording = await _externalMusicSearchService.SearchRecordingByIdAsync(id);

        return recording.ToResponse(_imageService);
    }

    public async Task<List<RecordingResponse>> SearchAlbumRecordingsByIdAsync(Guid albumId)
    {

        var recordings = await _externalMusicSearchService.SearchAlbumRecordingsByIdAsync(albumId);


        if (!recordings.Any())
            return new List<RecordingResponse>();

        return recordings.Select(recording => recording.ToResponse(_imageService)).ToList();
    }

    public async Task SearchAndSaveRecordingAsync(string query)
    {
        var recordings = await _externalMusicSearchService.SearchRecordingsAsync(query);
        var recordingToSave = recordings.First();

        await _dbContext.Recordings.AddAsync(recordingToSave.ToEntity(_imageService));
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
    const int maxRetries = 3;
    int retryCount = 0;

    while (retryCount < maxRetries)
    {
        try
        {
            await SaveAlbumRecordingsInternal(recordings);
            return; 
        }
        catch (DbUpdateException databaseUpdateException) when (databaseUpdateException.InnerException is PostgresException postgresException && postgresException.SqlState == "23505")
        {
            retryCount++;

            _dbContext.ChangeTracker.Clear();

            if (retryCount >= maxRetries)
            {
                throw new InvalidOperationException($"Failed to save album recordings after {maxRetries} attempts due to concurrent modifications.", databaseUpdateException);
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
    var release = releaseDto.ToEntity(_imageService);

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
        .Select(r => r.ToEntity(_imageService))
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

    public async Task RefreshAllAlbumCovers(CancellationToken cancellationToken = default)
    {
        var operation = await _dbContext.CoverMetadataRefreshes.SingleOrDefaultAsync(
            refresh => refresh.Id == CoverMetadataRefresh.OperationKey, cancellationToken);
        if (operation?.Pending == true) return;
        if (operation is null)
        {
            operation = new CoverMetadataRefresh();
            _dbContext.CoverMetadataRefreshes.Add(operation);
        }
        operation.Cursor = null;
        operation.Pending = true;
        operation.CompletedReleases = 0;
        operation.RequestedAt = DateTimeOffset.UtcNow;
        operation.CompletedAt = null;
        operation.Version = Guid.NewGuid();
        operation.LastFailure = string.Empty;
        try { await _dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { _dbContext.ChangeTracker.Clear(); }
        catch (DbUpdateException)
        {
            _dbContext.ChangeTracker.Clear();
            if (!await _dbContext.CoverMetadataRefreshes.AsNoTracking().AnyAsync(
                refresh => refresh.Id == CoverMetadataRefresh.OperationKey && refresh.Pending, cancellationToken)) throw;
        }
    }

    public Task<CoverMetadataRefresh?> GetCoverMetadataRefresh(CancellationToken cancellationToken = default) =>
        _dbContext.CoverMetadataRefreshes.AsNoTracking().SingleOrDefaultAsync(
            refresh => refresh.Id == CoverMetadataRefresh.OperationKey, cancellationToken);

    public async Task CancelCoverMetadataRefresh(CancellationToken cancellationToken = default)
    {
        await _dbContext.CoverMetadataRefreshes.Where(refresh => refresh.Id == CoverMetadataRefresh.OperationKey && refresh.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(refresh => refresh.Pending, false)
                .SetProperty(refresh => refresh.Version, Guid.NewGuid()), cancellationToken);
    }

    private const double FuzzySimilarityThreshold = 0.25;
    private const int FuzzyLimit = 10;

    public async Task<DbSearchResponse> DbSearch(string query, int page, int pageSize, Guid userId)
    {
        var lower = query.ToLower();
        var likedIds = await _libraryService.GetLikedRecordingIdsAsync(userId, CancellationToken.None);

        // ── Tier 1: substring match ─────────────────────────────────────────
        var rawTier1 = await _dbContext.Recordings
            .Include(r => r.Release).ThenInclude(r => r.Artist)
            .Where(r =>
                r.Title.ToLower().Contains(lower) ||
                r.Release.Artist.Name.ToLower().Contains(lower))
            .OrderBy(r => r.Title.ToLower() == lower ? 0 : r.Title.ToLower().StartsWith(lower) ? 1 : 2)
            .ThenBy(r => r.Title)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync();

        var hasMore = rawTier1.Count > pageSize;
        var tier1Tracks = rawTier1.Take(pageSize).ToList();
        var tier1TrackIds = tier1Tracks.Select(r => r.Id).ToHashSet();

        var albums = new List<ReleaseResponse>();
        var artists = new List<ArtistResponse>();
        var fuzzyTracks = new List<RecordingResponse>();
        var fuzzyAlbums = new List<ReleaseResponse>();
        var fuzzyArtists = new List<ArtistResponse>();

        if (page == 1)
        {
            var rawAlbums = await _dbContext.Releases
                .Include(r => r.Artist)
                .Where(r =>
                    r.Title != null && (r.Title.ToLower().Contains(lower) ||
                    r.Artist.Name.ToLower().Contains(lower)))
                .OrderBy(r => r.Title!.ToLower() == lower ? 0 : r.Title!.ToLower().StartsWith(lower) ? 1 : 2)
                .ThenBy(r => r.Title)
                .Take(pageSize)
                .ToListAsync();

            var rawArtists = await _dbContext.Artists
                .Where(a => a.Name.ToLower().Contains(lower))
                .OrderBy(a => a.Name.ToLower() == lower ? 0 : a.Name.ToLower().StartsWith(lower) ? 1 : 2)
                .ThenBy(a => a.Name)
                .Take(pageSize)
                .ToListAsync();

            albums = rawAlbums.Select(r => r.ToResponse(_imageService)).ToList();
            artists = rawArtists.Select(a => a.ToResponse()).ToList();

            var tier1AlbumIds = rawAlbums.Select(r => r.Id).ToHashSet();
            var tier1ArtistIds = rawArtists.Select(a => a.Id).ToHashSet();

            // ── Tier 2: trigram similarity for anything not caught by substring ──
            // Order by similarity of the combined "title artist" string so the
            // best-matching result sorts first regardless of which field matched.

            var rawFuzzyTracks = await _dbContext.Recordings
                .Include(r => r.Release).ThenInclude(r => r.Artist)
                .Where(r => !tier1TrackIds.Contains(r.Id))
                .Where(r =>
                    EF.Functions.TrigramsSimilarity(r.Title, query) > FuzzySimilarityThreshold ||
                    EF.Functions.TrigramsSimilarity(r.Release.Artist.Name, query) > FuzzySimilarityThreshold)
                .OrderByDescending(r =>
                    EF.Functions.TrigramsSimilarity(r.Title + " " + r.Release.Artist.Name, query))
                .Take(FuzzyLimit)
                .ToListAsync();

            fuzzyTracks = rawFuzzyTracks
                .Select(r => r.ToResponse(_imageService, likedIds.Contains(r.Id)))
                .ToList();

            var rawFuzzyAlbums = await _dbContext.Releases
                .Include(r => r.Artist)
                .Where(r => !tier1AlbumIds.Contains(r.Id) && r.Title != null)
                .Where(r =>
                    EF.Functions.TrigramsSimilarity(r.Title!, query) > FuzzySimilarityThreshold ||
                    EF.Functions.TrigramsSimilarity(r.Artist.Name, query) > FuzzySimilarityThreshold)
                .OrderByDescending(r =>
                    EF.Functions.TrigramsSimilarity(r.Title! + " " + r.Artist.Name, query))
                .Take(FuzzyLimit)
                .ToListAsync();

            fuzzyAlbums = rawFuzzyAlbums.Select(r => r.ToResponse(_imageService)).ToList();

            var rawFuzzyArtists = await _dbContext.Artists
                .Where(a => !tier1ArtistIds.Contains(a.Id))
                .Where(a => EF.Functions.TrigramsSimilarity(a.Name, query) > FuzzySimilarityThreshold)
                .OrderByDescending(a => EF.Functions.TrigramsSimilarity(a.Name, query))
                .Take(FuzzyLimit)
                .ToListAsync();

            fuzzyArtists = rawFuzzyArtists.Select(a => a.ToResponse()).ToList();
        }

        return new DbSearchResponse
        {
            Page = page,
            PageSize = pageSize,
            HasMoreTracks = hasMore,
            Tracks = tier1Tracks.Select(r => r.ToResponse(_imageService, likedIds.Contains(r.Id))).ToList(),
            Albums = albums,
            Artists = artists,
            FuzzyTracks = fuzzyTracks,
            FuzzyAlbums = fuzzyAlbums,
            FuzzyArtists = fuzzyArtists,
        };
    }

    public async Task<UniversalSearchResponse> GeneralSearch(string query, int limit, Guid userId)
    {
        var response = new UniversalSearchResponse();

        var playlistsTask = _dbContext.Playlists
            .Where(p => p.Name.ToLower().Contains(query.ToLower()))
            .Take(limit)
            .ToListAsync();

        var artistsTask = _externalMusicSearchService.SearchArtistsAsync(query);
        var recordingsTask = _externalMusicSearchService.SearchRecordingsAsync(query);
        var albumsTask = _externalMusicSearchService.SearchAlbumsAsync(query);
        var likedRecordingIdsTask = _libraryService.GetLikedRecordingIdsAsync(userId, CancellationToken.None);

        await Task.WhenAll(playlistsTask, artistsTask, recordingsTask, albumsTask, likedRecordingIdsTask);

        var playlists = playlistsTask.Result;
        var artists = artistsTask.Result ?? new List<ArtistDto>();
        var recordings = recordingsTask.Result ?? new List<RecordingDto>();
        var albums = albumsTask.Result ?? new List<ReleaseDto>();
        var likedRecordingIds = likedRecordingIdsTask.Result;

        response.Playlists = playlists;
        response.Artists = artists.Take(limit).Select(a => a.ToResponse()).ToList();
        response.Tracks = recordings.Take(limit).Select(r => r.ToResponse(_imageService, likedRecordingIds.Contains(r.Id))).ToList();

        response.Albums = albums
            .Take(limit)
            .Select(r => r.ToResponse(_imageService))
            .ToList();

        ArtistDto? bestArtist = artists.FirstOrDefault();
        RecordingDto? bestRecording = recordings.FirstOrDefault();
        ReleaseDto? bestAlbum = albums.FirstOrDefault();
        Playlist? bestPlaylist = playlists.FirstOrDefault(p => p.Name.Equals(query, StringComparison.OrdinalIgnoreCase));

        if (bestPlaylist != null)
        {
            response.BestMatch = new BestMatch { Type = "playlist", Data = bestPlaylist };
        }
        else
        {
            int artistScore = bestArtist?.Score ?? 0;
            int recordingScore = bestRecording?.Score ?? 0;
            int albumScore = bestAlbum?.Score ?? 0;

            if (artistScore >= recordingScore && artistScore >= albumScore && bestArtist != null)
            {
                response.BestMatch = new BestMatch { Type = "artist", Data = bestArtist.ToResponse() };
            }
            else if (recordingScore >= albumScore && bestRecording != null)
            {
                response.BestMatch = new BestMatch { Type = "track", Data = bestRecording.ToResponse(_imageService, likedRecordingIds.Contains(bestRecording.Id)) };
            }
            else if (bestAlbum != null)
            {
                response.BestMatch = new BestMatch { Type = "album", Data = bestAlbum.ToResponse(_imageService) };
            }
        }

        return response;
    }
}
