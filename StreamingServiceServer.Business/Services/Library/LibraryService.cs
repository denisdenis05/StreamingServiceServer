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
    private readonly IExternalMusicSearchService _externalMusicSearchService;
    const string DefaultPlaylistName = "New Playlist";

    public LibraryService(StreamingDbContext dbContext, IExternalMusicSearchService externalMusicSearchService)
    {
        _dbContext = dbContext;
        _externalMusicSearchService = externalMusicSearchService;
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
    
    public async Task<List<RecordingResponse>> GetPlaylistRecordingsAsync(Guid playlistId, Guid userId, CancellationToken cancellationToken)
    {
        var likedRecordingIds = await GetLikedRecordingIdsAsync(userId, cancellationToken);

        return await _dbContext.PlaylistRecordings
            .Where(playlistRecording => playlistRecording.PlaylistId == playlistId)
            .GroupJoin(
                _dbContext.Recordings,
                playlistRecording => playlistRecording.RecordingId,
                recording => recording.Id,
                (playlistRecording, recordings) => new { playlistRecording, recordings }
            )
            .SelectMany(
                x => x.recordings.DefaultIfEmpty(),
                (x, recording) => new { x.playlistRecording, recording }
            )
            .OrderBy(x => x.playlistRecording.Order)
            .Select(x => new RecordingResponse
            {
                Id = x.playlistRecording.RecordingId,
                Title = x.playlistRecording.Title,
                ArtistName = x.playlistRecording.Artist,
                ReleaseTitle = x.playlistRecording.Album,
                Cover = x.recording != null && !string.IsNullOrEmpty(x.recording.Cover) ? x.recording.Cover : x.playlistRecording.Cover,
                PositionInAlbum = x.playlistRecording.Order,
                IsLiked = likedRecordingIds.Contains(x.playlistRecording.RecordingId)
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ReleaseResponse>> GetLibraryAsync(Guid userId, CancellationToken cancellationToken)
    {
        var playlists = await _dbContext.Playlists
            .Include(p => p.Owner)
            .Where(p => p.OwnerId == userId)
            .ToListAsync(cancellationToken);

        var likedAlbums = await _dbContext.Releases
            .Include(r => r.Artist)
            .OrderBy(r => Guid.NewGuid())
            .Take(10)
            .ToListAsync(cancellationToken);

        var response = new List<ReleaseResponse>();

        response.AddRange(playlists.Select(p => new ReleaseResponse
        {
            Id = p.Id,
            Title = p.Name,
            ArtistName = p.Owner.Username,
            Cover = p.Cover,
            Type = "Playlist"
        }));

        response.AddRange(likedAlbums.Select(r => new ReleaseResponse
        {
            Id = r.Id,
            Title = r.Title,
            ArtistName = r.Artist.Name,
            Cover = !string.IsNullOrEmpty(r.OptimizedCover) ? r.OptimizedCover : r.Cover,
            SmallCover = !string.IsNullOrEmpty(r.OptimizedSmallCover) ? r.OptimizedSmallCover : r.SmallCover,
            VerySmallCover = !string.IsNullOrEmpty(r.OptimizedVerySmallCover) ? r.OptimizedVerySmallCover : r.VerySmallCover,
            Type = "Album"
        }));

        return response.OrderBy(x => x.Title).ToList();
    }

    public async Task RemoveRecordingFromPlaylistAsync(Guid userId, Guid playlistId, Guid recordingId, CancellationToken cancellationToken)
    {
        var playlist = await _dbContext.Playlists
            .FirstOrDefaultAsync(p => p.Id == playlistId && p.OwnerId == userId, cancellationToken);

        if (playlist == null)
        {
            throw new InvalidOperationException("Playlist not found or user is not the owner.");
        }

        var recordingToRemove = await _dbContext.PlaylistRecordings
            .FirstOrDefaultAsync(pr => pr.PlaylistId == playlistId && pr.RecordingId == recordingId, cancellationToken);

        if (recordingToRemove != null)
        {
            _dbContext.PlaylistRecordings.Remove(recordingToRemove);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task UpdatePlaylistAsync(Guid userId, Guid playlistId, string name, CancellationToken cancellationToken)
    {
        var playlist = await _dbContext.Playlists
            .FirstOrDefaultAsync(p => p.Id == playlistId && p.OwnerId == userId, cancellationToken);

        if (playlist == null)
        {
            throw new InvalidOperationException("Playlist not found or user is not the owner.");
        }

        if (playlist.IsSystem)
        {
            throw new InvalidOperationException("Cannot modify system playlists.");
        }

        playlist.Name = name;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRecordingsToPlaylistAsync(Guid userId, Guid playlistId, List<AddSongToPlaylistRequest> songs, CancellationToken cancellationToken)
    {
        var playlist = await _dbContext.Playlists
            .Include(p => p.PlaylistRecordings)
            .FirstOrDefaultAsync(p => p.Id == playlistId && p.OwnerId == userId, cancellationToken);

        if (playlist == null)
        {
            throw new InvalidOperationException("Playlist not found or user is not the owner.");
        }

        var currentMaxOrder = playlist.PlaylistRecordings.Any() 
            ? playlist.PlaylistRecordings.Max(pr => pr.Order) 
            : -1;

        foreach (var song in songs)
        {
            playlist.PlaylistRecordings.Add(new PlaylistRecording
            {
                PlaylistId = playlistId,
                RecordingId = song.Id,
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                Cover = song.Cover,
                Order = ++currentMaxOrder,
                AddedById = userId
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleLikeAsync(Guid userId, Guid recordingId, AddSongToPlaylistRequest songDetails, CancellationToken cancellationToken)
    {
        var likedPlaylist = await _dbContext.Playlists
            .Include(p => p.PlaylistRecordings)
            .FirstOrDefaultAsync(p => p.OwnerId == userId && p.IsSystem, cancellationToken);

        if (likedPlaylist == null)
        {
            likedPlaylist = new Playlist
            {
                Id = Guid.NewGuid(),
                Name = "Liked Songs",
                OwnerId = userId,
                IsSystem = true,
                Cover = string.Empty
            };
            _dbContext.Playlists.Add(likedPlaylist);
            // Save to ensure ID exists for recordings
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var existingRecording = likedPlaylist.PlaylistRecordings
            .FirstOrDefault(pr => pr.RecordingId == recordingId);

        if (existingRecording != null)
        {
            _dbContext.PlaylistRecordings.Remove(existingRecording);
        }
        else
        {
            var maxOrder = likedPlaylist.PlaylistRecordings.Any() 
                ? likedPlaylist.PlaylistRecordings.Max(pr => pr.Order) 
                : -1;

            likedPlaylist.PlaylistRecordings.Add(new PlaylistRecording
            {
                PlaylistId = likedPlaylist.Id,
                RecordingId = recordingId,
                Title = songDetails.Title,
                Artist = songDetails.Artist,
                Album = songDetails.Album,
                Cover = songDetails.Cover,
                Order = maxOrder + 1,
                AddedById = userId
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<HashSet<Guid>> GetLikedRecordingIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var likedPlaylist = await _dbContext.Playlists
            .Include(p => p.PlaylistRecordings)
            .FirstOrDefaultAsync(p => p.OwnerId == userId && p.IsSystem, cancellationToken);

        if (likedPlaylist == null)
        {
            return new HashSet<Guid>();
        }

        return likedPlaylist.PlaylistRecordings.Select(pr => pr.RecordingId).ToHashSet();
    }

    public async Task AddRecordingsToPlaylistFuzzyAsync(Guid userId, Guid playlistId, List<AddRecordingToPlaylistFuzzyRequest> requests, CancellationToken cancellationToken)
    {
        var playlist = await _dbContext.Playlists
            .Include(p => p.PlaylistRecordings)
            .FirstOrDefaultAsync(p => p.Id == playlistId && p.OwnerId == userId, cancellationToken);

        if (playlist == null)
        {
            throw new InvalidOperationException("Playlist not found or user is not the owner.");
        }

        var currentMaxOrder = playlist.PlaylistRecordings.Any() 
            ? playlist.PlaylistRecordings.Max(pr => pr.Order) 
            : -1;

        foreach (var request in requests)
        {
            var candidates = await _dbContext.Recordings
                .Include(r => r.ArtistCredit)
                .ThenInclude(ac => ac.Artist)
                .Include(r => r.Release)
                .Where(r => r.Title.ToLower().Contains(request.Name.ToLower()))
                .ToListAsync(cancellationToken);

            var bestMatch = candidates
                .Select(r => new 
                { 
                    Recording = r, 
                    NameScore = FuzzySharp.Fuzz.Ratio(r.Title.ToLower(), request.Name.ToLower()),
                    ArtistScore = r.ArtistCredit.Any() 
                        ? r.ArtistCredit.Max(ac => FuzzySharp.Fuzz.Ratio((ac.Name ?? ac.Artist?.Name ?? "").ToLower(), request.ArtistName.ToLower()))
                        : 0,
                    ReleaseScore = r.Release != null 
                        ? FuzzySharp.Fuzz.Ratio(r.Release.Title.ToLower(), request.ReleaseName.ToLower())
                        : 0
                })
                .Where(x => x.NameScore > 90 && (x.ArtistScore > 90 || x.ReleaseScore > 90))
                .OrderByDescending(x => x.NameScore + Math.Max(x.ArtistScore, x.ReleaseScore))
                .FirstOrDefault();

            Guid recordingId = Guid.Empty;
            string title = string.Empty;
            string artist = string.Empty;
            string album = string.Empty;
            string cover = string.Empty;
            bool found = false;

            if (bestMatch != null)
            {
                found = true;
                recordingId = bestMatch.Recording.Id;
                title = bestMatch.Recording.Title;
                artist = bestMatch.Recording.ArtistCredit.FirstOrDefault()?.Name 
                         ?? bestMatch.Recording.ArtistCredit.FirstOrDefault()?.Artist?.Name 
                         ?? "Unknown Artist";
                album = bestMatch.Recording.Release?.Title ?? "Unknown Album";
                cover = bestMatch.Recording.Cover;
            }
            else
            {
                var query = $"recording:\"{request.Name}\" AND artist:\"{request.ArtistName}\"";
                var externalRecordings = await _externalMusicSearchService.SearchRecordingsAsync(query);

                var bestExternalMatch = externalRecordings
                    .Select(r => new 
                    { 
                        Recording = r, 
                        NameScore = FuzzySharp.Fuzz.Ratio(r.Title.ToLower(), request.Name.ToLower()),
                        ArtistScore = r.ArtistCredit.Any() 
                            ? r.ArtistCredit.Max(ac => FuzzySharp.Fuzz.Ratio((ac.Name ?? ac.Artist?.Name ?? "").ToLower(), request.ArtistName.ToLower()))
                            : 0,
                        ReleaseScore = r.Releases != null && r.Releases.Any()
                            ? r.Releases.Max(rel => FuzzySharp.Fuzz.Ratio(rel.Title.ToLower(), request.ReleaseName.ToLower()))
                            : 0
                    })
                    .Where(x => x.NameScore > 90 && (x.ArtistScore > 90 || x.ReleaseScore > 90))
                    .OrderByDescending(x => x.NameScore + Math.Max(x.ArtistScore, x.ReleaseScore))
                    .FirstOrDefault();

                if (bestExternalMatch != null)
                {
                    found = true;
                    recordingId = bestExternalMatch.Recording.Id;
                    title = bestExternalMatch.Recording.Title ?? "Unknown Title";
                    artist = bestExternalMatch.Recording.ArtistCredit.FirstOrDefault()?.Name ?? "Unknown Artist";
                    album = bestExternalMatch.Recording.Releases.FirstOrDefault()?.Title ?? "Unknown Album";
                    cover = bestExternalMatch.Recording.Cover?.Cover ?? string.Empty;
                }
            }

            if (found)
            {
                playlist.PlaylistRecordings.Add(new PlaylistRecording
                {
                    PlaylistId = playlistId,
                    RecordingId = recordingId,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    Cover = cover,
                    Order = ++currentMaxOrder,
                    AddedById = userId
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}