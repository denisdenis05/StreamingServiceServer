using StreamingServiceServer.Business.Services.Images;
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
    private readonly IImageService? _images;
    const string DefaultPlaylistName = "New Playlist";

    public LibraryService(StreamingDbContext dbContext, IExternalMusicSearchService externalMusicSearchService, IImageService? images = null)
    {
        _dbContext = dbContext;
        _externalMusicSearchService = externalMusicSearchService;
        _images = images;
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
        var isSystemPlaylist = await _dbContext.Playlists
            .Where(p => p.Id == playlistId)
            .Select(p => p.IsSystem)
            .FirstOrDefaultAsync(cancellationToken);

        var query = from pr in _dbContext.PlaylistRecordings
                    where pr.PlaylistId == playlistId
                    join r in _dbContext.Recordings on pr.RecordingId equals r.Id into rGroup
                    from r in rGroup.DefaultIfEmpty()
                    join s in _dbContext.Recordings on pr.SuggestedRecordingId equals s.Id into sGroup
                    from s in sGroup.DefaultIfEmpty()
                    select new { pr, r, s };

        if (isSystemPlaylist)
        {
            query = query.OrderByDescending(x => x.pr.Order);
        }
        else
        {
            query = query.OrderBy(x => x.pr.Order);
        }

        var recordings = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(playlistRow => new
            {
                Response = new RecordingResponse
                {
                    Id = playlistRow.pr.RecordingId,
                    ReleaseId = playlistRow.r != null ? playlistRow.r.Release.Id : (Guid?)null,
                    Title = playlistRow.pr.Title,
                    ArtistName = playlistRow.pr.Artist,
                    ReleaseTitle = playlistRow.pr.Album,
                    PositionInAlbum = playlistRow.pr.Order,
                    IsLiked = false,
                    SuggestedRecordingId = playlistRow.pr.SuggestedRecordingId,
                    SuggestedTitle = playlistRow.s != null ? playlistRow.s.Title : null,
                    SuggestedArtist = playlistRow.s != null ? playlistRow.s.ArtistCredit.Select(credit => credit.Name ?? credit.Artist.Name).FirstOrDefault() : null,
                    SuggestedAlbum = playlistRow.s != null ? playlistRow.s.Release.Title : null
                },
                Original = playlistRow.r != null && playlistRow.r.Cover != null && playlistRow.r.Cover != string.Empty ? playlistRow.r.Cover : playlistRow.pr.Cover,
                Small = playlistRow.r != null ? playlistRow.r.SmallCover : null,
                Thumbnail = playlistRow.r != null ? playlistRow.r.VerySmallCover : null,
                Local = playlistRow.r != null ? playlistRow.r.LocalCover : null,
                LocalSmall = playlistRow.r != null ? playlistRow.r.LocalSmallCover : null,
                LocalThumbnail = playlistRow.r != null ? playlistRow.r.LocalVerySmallCover : null,
                Optimized = playlistRow.r != null ? playlistRow.r.OptimizedCover : null,
                OptimizedSmall = playlistRow.r != null ? playlistRow.r.OptimizedSmallCover : null,
                OptimizedThumbnail = playlistRow.r != null ? playlistRow.r.OptimizedVerySmallCover : null
            }).ToListAsync(cancellationToken);
        var likedRecordingIds = await GetLikedRecordingIdsAsync(userId, recordings.Select(recording => recording.Response.Id).ToArray(), cancellationToken);
        foreach (var recording in recordings)
        {
            recording.Response.IsLiked = likedRecordingIds.Contains(recording.Response.Id);
            recording.Response.Cover = SelectCover(recording.Local, recording.Optimized, recording.Original);
            recording.Response.SmallCover = SelectCover(recording.LocalSmall, recording.OptimizedSmall, recording.Small);
            recording.Response.VerySmallCover = SelectCover(recording.LocalThumbnail, recording.OptimizedThumbnail, recording.Thumbnail);
        }
        return recordings.Select(recording => recording.Response).ToList();
    }

    private string SelectCover(string? local, string? optimized, string? source) =>
        _images?.GetPreferredCover(local, optimized, source) ?? CoverPolicy.Select(local, optimized, source);

    public async Task<List<ReleaseResponse>> GetLibraryAsync(Guid userId, CancellationToken cancellationToken)
    {
        var playlists = await _dbContext.Playlists
            .Include(p => p.Owner)
            .Where(p => p.OwnerId == userId)
            .ToListAsync(cancellationToken);

        var savedAlbums = await _dbContext.UserSavedAlbums
            .Include(x => x.Release).ThenInclude(r => r.Artist)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.SavedAt)
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

        response.AddRange(savedAlbums.Select(x => new ReleaseResponse
        {
            Id = x.Release.Id,
            Title = x.Release.Title,
            ArtistName = x.Release.Artist.Name,
            Cover = _images?.GetPreferredCover(x.Release.LocalCover, x.Release.OptimizedCover, x.Release.Cover) ?? CoverPolicy.Select(x.Release.LocalCover, x.Release.OptimizedCover, x.Release.Cover),
            SmallCover = _images?.GetPreferredCover(x.Release.LocalSmallCover, x.Release.OptimizedSmallCover, x.Release.SmallCover) ?? CoverPolicy.Select(x.Release.LocalSmallCover, x.Release.OptimizedSmallCover, x.Release.SmallCover),
            VerySmallCover = _images?.GetPreferredCover(x.Release.LocalVerySmallCover, x.Release.OptimizedVerySmallCover, x.Release.VerySmallCover) ?? CoverPolicy.Select(x.Release.LocalVerySmallCover, x.Release.OptimizedVerySmallCover, x.Release.VerySmallCover),
            Type = "Album"
        }));

        return response.OrderBy(x => x.Title).ToList();
    }

    public async Task<bool> IsAlbumSavedAsync(Guid userId, Guid albumId, CancellationToken cancellationToken)
    {
        return await _dbContext.UserSavedAlbums
            .AnyAsync(x => x.UserId == userId && x.ReleaseId == albumId, cancellationToken);
    }

    public async Task<bool> ToggleSavedAlbumAsync(Guid userId, Guid albumId, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.UserSavedAlbums
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ReleaseId == albumId, cancellationToken);

        if (existing != null)
        {
            _dbContext.UserSavedAlbums.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        var releaseExists = await _dbContext.Releases.AnyAsync(r => r.Id == albumId, cancellationToken);
        if (!releaseExists)
            throw new InvalidOperationException($"Release {albumId} not found.");

        _dbContext.UserSavedAlbums.Add(new UserSavedAlbum { UserId = userId, ReleaseId = albumId });
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
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

    private IQueryable<Guid> LikedRecordingIds(Guid userId) => _dbContext.PlaylistRecordings.AsNoTracking()
        .Where(recording => recording.PlaylistId == _dbContext.Playlists
            .Where(playlist => playlist.OwnerId == userId && playlist.IsSystem)
            .Select(playlist => (Guid?)playlist.Id).FirstOrDefault())
        .Select(recording => recording.RecordingId);

    public async Task<HashSet<Guid>> GetLikedRecordingIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        (await LikedRecordingIds(userId).ToListAsync(cancellationToken)).ToHashSet();

    public async Task<HashSet<Guid>> GetLikedRecordingIdsAsync(Guid userId, IReadOnlyCollection<Guid> candidateRecordingIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidateRecordingIds.Count == 0) return new HashSet<Guid>();
        return (await LikedRecordingIds(userId).Where(identifier => candidateRecordingIds.Contains(identifier))
            .ToListAsync(cancellationToken)).ToHashSet();
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
            var (found, recordingId, title, artist, album, cover, _, _) =
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

    public async Task<List<PlaylistImportResult>> PreviewPlaylistImportAsync(
        List<AddRecordingToPlaylistFuzzyRequest> tracks,
        CancellationToken cancellationToken)
    {
        var results = new List<PlaylistImportResult>();
        foreach (var track in tracks)
        {
            var (found, recordingId, title, artist, album, cover, score, isExact) =
                await FuzzyMatchInDbAsync(track, cancellationToken);

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
                IsAvailable = found,
                IsExactMatch = found && isExact,
                IsSuggested = found && !isExact
            });
        }
        return results;
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

        var existingRecordingIds = playlist.PlaylistRecordings.Select(pr => pr.RecordingId).ToHashSet();
        var results = new List<PlaylistImportResult>();

        foreach (var track in tracks)
        {
            var (found, recordingId, title, artist, album, cover, score, isExact) =
                await FuzzyMatchInDbAsync(track, cancellationToken);

            bool userAccepted = track.AcceptSuggestion || track.ConfirmedRecordingId.HasValue;
            Guid targetRecordingId = track.ConfirmedRecordingId ?? recordingId;

            bool useMatchedRecording = found && (isExact || userAccepted);

            Guid dbRecordingId;
            if (useMatchedRecording && targetRecordingId != Guid.Empty && !existingRecordingIds.Contains(targetRecordingId))
            {
                dbRecordingId = targetRecordingId;
                existingRecordingIds.Add(dbRecordingId);
            }
            else
            {
                dbRecordingId = Guid.NewGuid();
            }

            var recordingEntry = new PlaylistRecording
            {
                PlaylistId = playlistId,
                RecordingId = dbRecordingId,
                Title = useMatchedRecording ? title : track.Name,
                Artist = useMatchedRecording ? artist : track.ArtistName,
                Album = useMatchedRecording ? album : track.ReleaseName,
                Cover = useMatchedRecording ? cover : string.Empty,
                Order = ++currentMaxOrder,
                AddedById = userId,
                SuggestedRecordingId = (!useMatchedRecording && found && !isExact) ? recordingId : null
            };

            playlist.PlaylistRecordings.Add(recordingEntry);

            results.Add(new PlaylistImportResult
            {
                OriginalName = track.Name,
                OriginalArtist = track.ArtistName,
                OriginalAlbum = track.ReleaseName,
                MatchedRecordingId = useMatchedRecording ? dbRecordingId : Guid.Empty,
                MatchedTitle = useMatchedRecording ? title : string.Empty,
                MatchedArtist = useMatchedRecording ? artist : string.Empty,
                MatchedAlbum = useMatchedRecording ? album : string.Empty,
                Cover = useMatchedRecording ? cover : string.Empty,
                MatchScore = score,
                IsAvailable = useMatchedRecording,
                IsExactMatch = isExact,
                IsSuggested = found && !isExact
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

    private async Task<(bool Found, Guid RecordingId, string Title, string Artist, string Album, string Cover, double Score, bool IsExactMatch)>
        FuzzyMatchInDbAsync(AddRecordingToPlaylistFuzzyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return (false, Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0, false);

        var reqTitle = request.Name.Trim();
        var reqArtist = request.ArtistName?.Trim() ?? string.Empty;
        var reqRelease = request.ReleaseName?.Trim() ?? string.Empty;

        var stripped = StripFeatureInfo(reqTitle);
        var useStripped = !string.Equals(reqTitle, stripped, StringComparison.OrdinalIgnoreCase);

        IQueryable<Recording> baseQuery = _dbContext.Recordings.AsNoTracking()
            .Include(r => r.ArtistCredit).ThenInclude(ac => ac.Artist)
            .Include(r => r.Release).ThenInclude(rel => rel.Artist);

        if (!string.IsNullOrEmpty(reqArtist))
        {
            var artistLower = reqArtist.ToLower();
            baseQuery = baseQuery.Where(r =>
                r.ArtistCredit.Any(ac => (ac.Name ?? ac.Artist.Name).ToLower().Contains(artistLower)) ||
                (r.Release != null && r.Release.Artist != null &&
                 r.Release.Artist.Name.ToLower().Contains(artistLower)));
        }

        var candidates = await baseQuery
            .Where(r => r.Title.ToLower().Contains(reqTitle.ToLower()) ||
                        (useStripped && r.Title.ToLower().Contains(stripped.ToLower())) ||
                        EF.Functions.TrigramsSimilarity(r.Title, reqTitle) > 0.20 ||
                        (useStripped && EF.Functions.TrigramsSimilarity(r.Title, stripped) > 0.20))
            .Take(50)
            .ToListAsync(cancellationToken);

        if (!candidates.Any())
        {
            IQueryable<Recording> fallbackQuery = _dbContext.Recordings.AsNoTracking()
                .Include(r => r.ArtistCredit).ThenInclude(ac => ac.Artist)
                .Include(r => r.Release).ThenInclude(rel => rel.Artist);

            if (!string.IsNullOrEmpty(reqArtist))
            {
                var artistLower = reqArtist.ToLower();
                fallbackQuery = fallbackQuery.Where(r =>
                    r.ArtistCredit.Any(ac => (ac.Name ?? ac.Artist.Name).ToLower().Contains(artistLower)) ||
                    (r.Release != null && r.Release.Artist != null &&
                     r.Release.Artist.Name.ToLower().Contains(artistLower)));
            }

            candidates = await fallbackQuery
                .Where(r => EF.Functions.TrigramsSimilarity(r.Title, reqTitle) > 0.15 ||
                            (useStripped && EF.Functions.TrigramsSimilarity(r.Title, stripped) > 0.15))
                .Take(30)
                .ToListAsync(cancellationToken);
        }

        if (!candidates.Any())
            return (false, Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0, false);

        var scored = candidates.Select(r =>
        {
            var artistName = r.ArtistCredit.FirstOrDefault()?.Name
                          ?? r.ArtistCredit.FirstOrDefault()?.Artist?.Name
                          ?? r.Release?.Artist?.Name
                          ?? string.Empty;

            int artistScore = !string.IsNullOrEmpty(reqArtist)
                ? (r.ArtistCredit.Any()
                    ? r.ArtistCredit.Max(ac =>
                        FuzzySharp.Fuzz.Ratio(
                            (ac.Name ?? ac.Artist?.Name ?? string.Empty).ToLower(),
                            reqArtist.ToLower()))
                    : (!string.IsNullOrEmpty(r.Release?.Artist?.Name)
                        ? FuzzySharp.Fuzz.Ratio(r.Release.Artist.Name.ToLower(), reqArtist.ToLower())
                        : 0))
                : 100;

            int titleRatio = FuzzySharp.Fuzz.Ratio(r.Title.ToLower(), reqTitle.ToLower());
            int titleTokenSort = FuzzySharp.Fuzz.TokenSortRatio(r.Title.ToLower(), reqTitle.ToLower());
            int strippedRatio = useStripped ? FuzzySharp.Fuzz.Ratio(r.Title.ToLower(), stripped.ToLower()) : 0;
            int strippedToken = useStripped ? FuzzySharp.Fuzz.TokenSortRatio(r.Title.ToLower(), stripped.ToLower()) : 0;
            double titleScore = Math.Max(Math.Max(titleRatio, titleTokenSort),
                Math.Max(strippedRatio, strippedToken));

            double releaseScore = (!string.IsNullOrEmpty(r.Release?.Title) && !string.IsNullOrEmpty(reqRelease))
                ? (double)FuzzySharp.Fuzz.TokenSortRatio(r.Release.Title.ToLower(), reqRelease.ToLower())
                : 50.0;

            bool passesArtist = string.IsNullOrEmpty(reqArtist) || artistScore >= 50;
            bool passesTitle = titleScore > 65.0;

            double totalScore = (titleScore * 0.40) + (artistScore * 0.30) + (releaseScore * 0.30);

            bool isTitleExact = string.Equals(r.Title.Trim(), reqTitle, StringComparison.OrdinalIgnoreCase) ||
                                (useStripped && string.Equals(r.Title.Trim(), stripped, StringComparison.OrdinalIgnoreCase)) ||
                                titleScore >= 85.0;

            bool isArtistExact = string.IsNullOrEmpty(reqArtist) ||
                                 string.Equals(artistName.Trim(), reqArtist, StringComparison.OrdinalIgnoreCase) ||
                                 artistScore >= 70;

            bool isAlbumGood = string.IsNullOrEmpty(reqRelease) || releaseScore >= 60.0;

            bool isExactMatch = isTitleExact && isArtistExact && (isAlbumGood || (titleScore >= 90.0 && artistScore >= 85));

            return new
            {
                Recording = r,
                TitleScore = titleScore,
                ArtistScore = artistScore,
                ReleaseScore = releaseScore,
                PassesArtist = passesArtist,
                PassesTitle = passesTitle,
                IsExactMatch = isExactMatch,
                TotalScore = totalScore
            };
        }).Where(x => x.PassesArtist && x.PassesTitle).ToList();

        var bestExact = scored
            .Where(x => x.IsExactMatch)
            .OrderByDescending(x => x.TotalScore)
            .FirstOrDefault();

        if (bestExact != null)
        {
            var (found, recId, title, artist, album, cover, score) = BuildMatch(bestExact.Recording, bestExact.TotalScore);
            return (true, recId, title, artist, album, cover, score, true);
        }

        var bestPartial = scored
            .OrderByDescending(x => x.TotalScore)
            .FirstOrDefault();

        if (bestPartial != null)
        {
            var (found, recId, title, artist, album, cover, score) = BuildMatch(bestPartial.Recording, bestPartial.TotalScore);
            return (true, recId, title, artist, album, cover, score, false);
        }

        return (false, Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0, false);
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
