using System.Net.Http.Json;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Services.MusicSearch;

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

    public async Task<List<RecordingResponse>> GetPlaylistRecordingsAsync(Guid playlistId, Guid userId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var likedRecordingIds = await GetLikedRecordingIdsAsync(userId, cancellationToken);

        var isSystemPlaylist = await _dbContext.Playlists
            .Where(p => p.Id == playlistId)
            .Select(p => p.IsSystem)
            .FirstOrDefaultAsync(cancellationToken);

        var query = _dbContext.PlaylistRecordings
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
            );

        if (isSystemPlaylist)
        {
            query = query.OrderByDescending(x => x.playlistRecording.Order);
        }
        else
        {
            query = query.OrderBy(x => x.playlistRecording.Order);
        }

        return await query
            .Select(x => new RecordingResponse
            {
                Id = x.playlistRecording.RecordingId,
                ReleaseId = x.recording != null ? x.recording.Release.Id : (Guid?)null,
                Title = x.playlistRecording.Title,
                ArtistName = x.playlistRecording.Artist,
                ReleaseTitle = x.playlistRecording.Album,
                Cover = x.recording != null && !string.IsNullOrEmpty(x.recording.Cover) ? x.recording.Cover : x.playlistRecording.Cover,
                PositionInAlbum = x.playlistRecording.Order,
                IsLiked = likedRecordingIds.Contains(x.playlistRecording.RecordingId)
            })
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
            throw new InvalidOperationException("Playlist not found or user is not the owner.");

        var currentMaxOrder = playlist.PlaylistRecordings.Any()
            ? playlist.PlaylistRecordings.Max(pr => pr.Order)
            : -1;

        foreach (var request in requests)
        {
            var (found, recordingId, title, artist, album, cover, _) =
                await FuzzyMatchInDbAsync(request, cancellationToken);

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

    public async Task<List<PlaylistImportResult>> ImportPlaylistFromJsonAsync(
        Guid userId,
        Guid playlistId,
        List<AddRecordingToPlaylistFuzzyRequest> tracks,
        CancellationToken cancellationToken)
    {
        var playlist = await _dbContext.Playlists
            .Include(p => p.PlaylistRecordings)
            .FirstOrDefaultAsync(p => p.Id == playlistId && p.OwnerId == userId, cancellationToken)
            ?? throw new InvalidOperationException("Playlist not found or user is not the owner.");

        var currentMaxOrder = playlist.PlaylistRecordings.Any()
            ? playlist.PlaylistRecordings.Max(pr => pr.Order)
            : -1;

        var results = new List<PlaylistImportResult>();

        foreach (var track in tracks)
        {
            var (found, recordingId, title, artist, album, cover, score) =
                await FuzzyMatchInDbAsync(track, cancellationToken);

            // Always add the entry — unavailable tracks use Guid.Empty so the user can replace them later.
            var recordingEntry = new PlaylistRecording
            {
                PlaylistId = playlistId,
                RecordingId = found ? recordingId : Guid.Empty,
                Title = found ? title : track.Name,
                Artist = found ? artist : track.ArtistName,
                Album = found ? album : track.ReleaseName,
                Cover = found ? cover : string.Empty,
                Order = ++currentMaxOrder,
                AddedById = userId
            };

            // Skip duplicate RecordingId=Guid.Empty entries would break the composite PK.
            // Use a unique negative sentinel via Order — Order is unique per playlist row.
            playlist.PlaylistRecordings.Add(recordingEntry);

            results.Add(new PlaylistImportResult
            {
                OriginalName = track.Name,
                OriginalArtist = track.ArtistName,
                OriginalAlbum = track.ReleaseName,
                MatchedRecordingId = found ? recordingId : Guid.Empty,
                MatchedTitle = found ? title : string.Empty,
                MatchedArtist = found ? artist : string.Empty,
                MatchedAlbum = found ? album : string.Empty,
                Cover = found ? cover : string.Empty,
                MatchScore = score,
                IsAvailable = found
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return results;
    }

    public async Task ReplacePlaylistRecordingAsync(
        Guid userId,
        Guid playlistId,
        Guid oldRecordingId,
        AddSongToPlaylistRequest newSong,
        CancellationToken cancellationToken)
    {
        var playlist = await _dbContext.Playlists
            .Include(p => p.PlaylistRecordings)
            .FirstOrDefaultAsync(p => p.Id == playlistId && p.OwnerId == userId, cancellationToken)
            ?? throw new InvalidOperationException("Playlist not found or user is not the owner.");

        var entry = playlist.PlaylistRecordings
            .FirstOrDefault(pr => pr.RecordingId == oldRecordingId)
            ?? throw new InvalidOperationException("Recording not found in playlist.");

        // To change the PK we must remove and re-add (composite PK = PlaylistId + RecordingId).
        var order = entry.Order;
        _dbContext.PlaylistRecordings.Remove(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _dbContext.PlaylistRecordings.Add(new PlaylistRecording
        {
            PlaylistId = playlistId,
            RecordingId = newSong.Id,
            Title = newSong.Title,
            Artist = newSong.Artist,
            Album = newSong.Album,
            Cover = newSong.Cover,
            Order = order,
            AddedById = userId
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // ── Shared DB-only fuzzy-match helper ────────────────────────────────────────
    // Returns (found, recordingId, title, artist, album, cover, score).
    // Tier-1: EF Contains (exact substring). Tier-2: pg_trgm similarity.
    // MusicBrainz is never called.
    private async Task<(bool Found, Guid RecordingId, string Title, string Artist, string Album, string Cover, double Score)>
        FuzzyMatchInDbAsync(AddRecordingToPlaylistFuzzyRequest request, CancellationToken cancellationToken)
    {
        var stripped = StripFeatureInfo(request.Name);
        var useStripped = !string.Equals(request.Name, stripped, StringComparison.OrdinalIgnoreCase);

        // Tier-1: substring match
        var t1Query = _dbContext.Recordings
            .Include(r => r.ArtistCredit).ThenInclude(ac => ac.Artist)
            .Include(r => r.Release).ThenInclude(rel => rel.Artist)
            .Where(r => r.Title.ToLower().Contains(request.Name.ToLower())
                     || (useStripped && r.Title.ToLower().Contains(stripped.ToLower())));

        var tier1 = await t1Query.ToListAsync(cancellationToken);

        var bestT1 = tier1
            .Select(r => new
            {
                Recording = r,
                NameScore = Math.Max(
                    FuzzySharp.Fuzz.Ratio(r.Title.ToLower(), request.Name.ToLower()),
                    useStripped ? FuzzySharp.Fuzz.Ratio(r.Title.ToLower(), stripped.ToLower()) : 0),
                ArtistScore = r.ArtistCredit.Any()
                    ? r.ArtistCredit.Max(ac => FuzzySharp.Fuzz.Ratio((ac.Name ?? ac.Artist?.Name ?? "").ToLower(), request.ArtistName.ToLower()))
                    : (r.Release?.Artist != null ? FuzzySharp.Fuzz.Ratio(r.Release.Artist.Name.ToLower(), request.ArtistName.ToLower()) : 0),
                ReleaseScore = r.Release != null
                    ? FuzzySharp.Fuzz.Ratio((r.Release.Title ?? "").ToLower(), request.ReleaseName.ToLower())
                    : 0
            })
            .Where(x => x.NameScore >= 80 && (x.ArtistScore >= 70 || x.ReleaseScore >= 70))
            .OrderByDescending(x => x.NameScore + Math.Max(x.ArtistScore, x.ReleaseScore))
            .FirstOrDefault();

        if (bestT1 != null)
        {
            return BuildMatch(bestT1.Recording, bestT1.NameScore + Math.Max(bestT1.ArtistScore, bestT1.ReleaseScore));
        }

        // Tier-2: pg_trgm similarity
        const double SimilarityThreshold = 0.25;
        var tier2 = await _dbContext.Recordings
            .Include(r => r.ArtistCredit).ThenInclude(ac => ac.Artist)
            .Include(r => r.Release).ThenInclude(rel => rel.Artist)
            .Where(r =>
                EF.Functions.TrigramsSimilarity(r.Title, request.Name) > SimilarityThreshold ||
                (useStripped && EF.Functions.TrigramsSimilarity(r.Title, stripped) > SimilarityThreshold))
            .OrderByDescending(r =>
                EF.Functions.TrigramsSimilarity(r.Title + " " + (r.Release != null ? r.Release.Artist.Name : ""), request.Name + " " + request.ArtistName))
            .Take(10)
            .ToListAsync(cancellationToken);

        var bestT2 = tier2
            .Select(r => new
            {
                Recording = r,
                NameScore = Math.Max(
                    FuzzySharp.Fuzz.Ratio(r.Title.ToLower(), request.Name.ToLower()),
                    useStripped ? FuzzySharp.Fuzz.Ratio(r.Title.ToLower(), stripped.ToLower()) : 0),
                ArtistScore = r.ArtistCredit.Any()
                    ? r.ArtistCredit.Max(ac => FuzzySharp.Fuzz.Ratio((ac.Name ?? ac.Artist?.Name ?? "").ToLower(), request.ArtistName.ToLower()))
                    : (r.Release?.Artist != null ? FuzzySharp.Fuzz.Ratio(r.Release.Artist.Name.ToLower(), request.ArtistName.ToLower()) : 0),
                ReleaseScore = r.Release != null
                    ? FuzzySharp.Fuzz.Ratio((r.Release.Title ?? "").ToLower(), request.ReleaseName.ToLower())
                    : 0
            })
            .Where(x => x.NameScore >= 70 && (x.ArtistScore >= 60 || x.ReleaseScore >= 60))
            .OrderByDescending(x => x.NameScore + Math.Max(x.ArtistScore, x.ReleaseScore))
            .FirstOrDefault();

        if (bestT2 != null)
        {
            return BuildMatch(bestT2.Recording, bestT2.NameScore + Math.Max(bestT2.ArtistScore, bestT2.ReleaseScore));
        }

        return (false, Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0);
    }

    private static (bool, Guid, string, string, string, string, double) BuildMatch(Recording r, double score)
    {
        var title = r.Title;
        var artist = r.ArtistCredit.FirstOrDefault()?.Name
                  ?? r.ArtistCredit.FirstOrDefault()?.Artist?.Name
                  ?? r.Release?.Artist?.Name
                  ?? "Unknown Artist";
        var album = r.Release?.Title ?? "Unknown Album";
        var cover = r.Cover ?? string.Empty;
        return (true, r.Id, title, artist, album, cover, score);
    }

    public async Task DeletePlaylistAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken)
    {
        var playlist = await _dbContext.Playlists
            .Include(p => p.PlaylistRecordings)
            .FirstOrDefaultAsync(p => p.Id == playlistId && p.OwnerId == userId, cancellationToken)
            ?? throw new InvalidOperationException("Playlist not found or user is not the owner.");

        _dbContext.PlaylistRecordings.RemoveRange(playlist.PlaylistRecordings);
        _dbContext.Playlists.Remove(playlist);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private string StripFeatureInfo(string title)
    {
        if (string.IsNullOrEmpty(title)) return title;
        var pattern = @"\s*[\[\(]\s*(feat|ft)\.?\s+.*?[\]\)]";
        return System.Text.RegularExpressions.Regex.Replace(title, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
    }
}
