using System.Net.Http.Json;
using Npgsql;
using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Services.MusicSearch;
using Microsoft.EntityFrameworkCore;

public class LibraryService : ILibraryService
{
    private readonly StreamingDbContext _dbContext;
    const string DefaultPlaylistName = "New Playlist";

    public LibraryService(StreamingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Playlist> CreatePlaylistAsync(Guid ownerId, CreatePlaylistRequest request, CancellationToken cancellationToken)
    {
        var ownerExists = await _dbContext.Users
            .AnyAsync(u => u.Id == ownerId, cancellationToken);
        if (!ownerExists)
        {
            throw new InvalidOperationException($"User with Id {ownerId} does not exist.");
        }

        var playlistId = Guid.NewGuid();
        while (await _dbContext.Playlists.AnyAsync(p => p.Id == playlistId, cancellationToken))
        {
            playlistId = Guid.NewGuid();
        }

        var recordings = await _dbContext.Recordings
            .Where(r => request.RecordingIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Title })
            .ToListAsync(cancellationToken);

        string playlistName;
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            playlistName = request.Name;
        }
        else if (recordings.Any())
        {
            playlistName = recordings.First().Title ?? DefaultPlaylistName;
        }
        else
        {
            playlistName = DefaultPlaylistName;
        }

        var playlist = new Playlist
        {
            Id = playlistId,
            Name = playlistName,
            OwnerId = ownerId,
            Cover = string.Empty
        };

        int order = 0;
        foreach (var recording in recordings)
        {
            playlist.PlaylistRecordings.Add(new PlaylistRecording
            {
                PlaylistId = playlist.Id,
                RecordingId = recording.Id,
                Order = order++,
                AddedById = ownerId
            });
        }
        
        _dbContext.Playlists.Add(playlist);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return playlist;
    }
    
    public async Task<List<PlaylistResponse>> GetUserPlaylistsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Playlists
            .Include(playlist => playlist.Owner )
            .Include(p => p.PlaylistRecordings)
            .Where(playlist => playlist.OwnerId == userId)
            .Select(playlist => playlist.ToResponse())
            .ToListAsync(cancellationToken);
    }
    
    public async Task<List<RecordingResponse>> GetPlaylistRecordingsAsync(Guid playlistId, CancellationToken cancellationToken)
    {
        return await _dbContext.PlaylistRecordings
            .Where(playlistRecording => playlistRecording.PlaylistId == playlistId)
            .OrderBy(playlistRecording => playlistRecording.Order)
            .Select(playlistRecording => playlistRecording.ToResponse())
            .ToListAsync(cancellationToken);
    }

}