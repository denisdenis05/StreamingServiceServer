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
        var recordings =  await _externalMusicSearchService.SearchRecordingsAsync(query);
        
        await _dbContext.Recordings.AddAsync(recordings.First().ToEntity());
        await _dbContext.SaveChangesAsync();
    }
    
    public async Task SearchAndSaveArtistAsync(string query)
    {
        var artists =  await _externalMusicSearchService.SearchArtistsAsync(query);
        
        await _dbContext.Artists.AddAsync(artists.First().ToEntity());
        await _dbContext.SaveChangesAsync();
    }
    
    public async Task SearchAndSaveAlbumRecordingsAsync(string query)
    {
        var recordings =  await _externalMusicSearchService.SearchAlbumRecordingsAsync(query);
        var release = recordings.First().Releases.First().ToEntity();
        await AddReleaseToDatabase(release);
        
        await AddArtistsToDatabaseFromRecordings(recordings);
        
        await _dbContext.Recordings.AddRangeAsync(recordings.Select(recording =>
        {
            var recordingToAdd = recording.ToEntity();
            recordingToAdd.Release = release;
            
            return recordingToAdd;
        }).ToList());
        await _dbContext.SaveChangesAsync();
    }
    
    public async Task SearchAndSaveAlbumRecordingsByIdAsync(Guid albumId)
    {
        var recordings =  await _externalMusicSearchService.SearchAlbumRecordingsByIdAsync(albumId);
        var release = recordings.First().Releases.First().ToEntity();
        await AddReleaseToDatabase(release);
        
        await AddArtistsToDatabaseFromRecordings(recordings);
        
        await _dbContext.Recordings.AddRangeAsync(recordings.Select(recording =>
        {
            var recordingToAdd = recording.ToEntity();
            recordingToAdd.Release = release;
            
            return recordingToAdd;
        }).ToList());
        await _dbContext.SaveChangesAsync();
    }

    private async Task AddReleaseToDatabase(Release release)
    {
        var releaseArtist = release.Artist;

        if (releaseArtist != null)
        {
            var artistExists = await _dbContext.Artists.AnyAsync(a => a.Id == releaseArtist.Id);
            if (!artistExists)
            {
                await _dbContext.Artists.AddAsync(releaseArtist);
            }
        }
        await _dbContext.Releases.AddAsync(release);
        await _dbContext.SaveChangesAsync();
    }

    private async Task AddArtistsToDatabaseFromRecordings(ICollection<RecordingDto> recordings)
    {
        var uniqueArtists = recordings
            .SelectMany(r => r.ArtistCredit)
            .Where(ac => ac.Artist != null)
            .Select(ac => ac.Artist!)
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .ToList();
        var existingArtistIds = await _dbContext.Artists
            .Where(a => uniqueArtists.Select(ua => ua.Id).Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync();

        foreach (var artistDto in uniqueArtists)
        {
            if (!existingArtistIds.Contains(artistDto.Id))
            {
                await _dbContext.Artists.AddAsync(artistDto.ToEntity());
            }
        }
        
        await _dbContext.SaveChangesAsync();
    }
}