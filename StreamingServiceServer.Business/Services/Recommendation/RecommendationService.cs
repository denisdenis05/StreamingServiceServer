using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Business.Constants;
using StreamingServiceServer.Business.Models;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.Recommendation;

public class RecommendationService : IRecommendationService
{
    private readonly StreamingDbContext _database;
    private readonly IImageService _imageService;

    public RecommendationService(StreamingDbContext database, IImageService imageService)
    {
        _database = database;
        _imageService = imageService;
    }

    public async Task<List<RecommendationSection>> GetSections(Guid userId)
    {
        var sections = new List<RecommendationSection>();
        if (await RecentCollections(userId).AnyAsync())
            sections.Add(new RecommendationSection { Id = RecommendationConstants.Recent, Title = RecommendationConstants.RecentTitle });
        if (await SavedAlbums(userId).AnyAsync())
            sections.Add(new RecommendationSection { Id = RecommendationConstants.Saved, Title = RecommendationConstants.SavedTitle });
        if (await ArtistAlbums(userId).AnyAsync())
            sections.Add(new RecommendationSection { Id = RecommendationConstants.Artists, Title = RecommendationConstants.ArtistsTitle });
        if (await _database.Releases.AnyAsync())
            sections.Add(new RecommendationSection { Id = RecommendationConstants.Latest, Title = RecommendationConstants.LatestTitle });
        return sections;
    }

    public async Task<List<ReleaseResponse>> GetSection(string sectionId, Guid userId)
    {
        switch (sectionId)
        {
            case RecommendationConstants.Recent:
                return await GetRecentQuick(userId, RecommendationConstants.SectionItemLimit);
            case RecommendationConstants.Saved:
                return await GetSavedAlbums(userId);
            case RecommendationConstants.Artists:
                return await GetArtistAlbums(userId);
            case RecommendationConstants.Today:
            case RecommendationConstants.Picks:
                var artistAlbums = await GetArtistAlbums(userId);
                return artistAlbums.Any() ? artistAlbums : await GetLatestAlbums();
            case RecommendationConstants.Latest:
                return await GetLatestAlbums();
            default:
                return [];
        }
    }

    public async Task<List<ReleaseResponse>> GetRecentQuick(Guid userId, int count)
    {
        if (count <= default(int)) return [];
        var boundedCount = Math.Min(count, RecommendationConstants.MaximumQuickItemLimit);
        var recentCollections = await RecentCollections(userId)
            .OrderByDescending(collection => collection.LastPlayedAt)
            .ThenBy(collection => collection.Type)
            .ThenBy(collection => collection.Identifier)
            .Take(boundedCount)
            .ToListAsync();
        var albumIdentifiers = recentCollections.Where(collection => collection.Type == RecommendationConstants.AlbumType)
            .Select(collection => collection.Identifier).ToArray();
        var playlistIdentifiers = recentCollections.Where(collection => collection.Type == RecommendationConstants.PlaylistType)
            .Select(collection => collection.Identifier).ToArray();
        var albums = await _database.Releases.AsNoTracking().Include(release => release.Artist)
            .Where(release => albumIdentifiers.Contains(release.Id)).ToDictionaryAsync(release => release.Id);
        var playlists = await _database.Playlists.AsNoTracking().Include(playlist => playlist.Owner)
            .Where(playlist => playlistIdentifiers.Contains(playlist.Id) && playlist.OwnerId == userId)
            .ToDictionaryAsync(playlist => playlist.Id);
        var response = new List<ReleaseResponse>();
        foreach (var collection in recentCollections)
        {
            if (collection.Type == RecommendationConstants.AlbumType && albums.TryGetValue(collection.Identifier, out var album))
                response.Add(album.ToResponse(_imageService));
            else if (collection.Type == RecommendationConstants.PlaylistType && playlists.TryGetValue(collection.Identifier, out var playlist))
                response.Add(new ReleaseResponse
                {
                    Id = playlist.Id,
                    Title = playlist.Name,
                    ArtistName = playlist.Owner.Username,
                    Cover = playlist.Cover,
                    Type = RecommendationConstants.PlaylistType,
                    CanEdit = true
                });
        }
        return response;
    }

    private IQueryable<RecentCollection> RecentCollections(Guid userId)
    {
        var albumCollections = _database.Listens.AsNoTracking()
            .Where(listen => listen.UserId == userId && listen.PlaylistId == null && listen.SourceAlbumId != null)
            .Join(_database.Releases, listen => listen.SourceAlbumId, release => (Guid?)release.Id,
                (listen, release) => new { Identifier = release.Id, listen.StartTime })
            .GroupBy(listen => listen.Identifier)
            .Select(group => new RecentCollection
            {
                Identifier = group.Key,
                Type = RecommendationConstants.AlbumType,
                LastPlayedAt = group.Max(listen => listen.StartTime)
            });
        var playlistCollections = _database.Listens.AsNoTracking()
            .Where(listen => listen.UserId == userId && listen.PlaylistId != null)
            .Join(_database.Playlists.Where(playlist => playlist.OwnerId == userId),
                listen => listen.PlaylistId, playlist => (Guid?)playlist.Id,
                (listen, playlist) => new { Identifier = playlist.Id, listen.StartTime })
            .GroupBy(listen => listen.Identifier)
            .Select(group => new RecentCollection
            {
                Identifier = group.Key,
                Type = RecommendationConstants.PlaylistType,
                LastPlayedAt = group.Max(listen => listen.StartTime)
            });
        return albumCollections.Concat(playlistCollections);
    }

    private IQueryable<StreamingServiceServer.Data.Models.Library.UserSavedAlbum> SavedAlbums(Guid userId) =>
        _database.UserSavedAlbums.AsNoTracking().Where(savedAlbum => savedAlbum.UserId == userId);

    private IQueryable<Release> ArtistAlbums(Guid userId)
    {
        var listenedArtistIdentifiers = _database.Listens.Where(listen => listen.UserId == userId)
            .Join(_database.Recordings, listen => listen.RecordingId, recording => recording.Id,
                (listen, recording) => recording.Release.Artist.Id);
        var savedArtistIdentifiers = SavedAlbums(userId).Select(savedAlbum => savedAlbum.Release.Artist.Id);
        var likedPlaylistIdentifier = _database.Playlists.Where(playlist => playlist.OwnerId == userId && playlist.IsSystem)
            .OrderBy(playlist => playlist.CreatedAt).ThenBy(playlist => playlist.Id)
            .Select(playlist => (Guid?)playlist.Id).Take(RecommendationConstants.LikedPlaylistSelectionLimit);
        var likedArtistIdentifiers = _database.PlaylistRecordings
            .Where(recording => likedPlaylistIdentifier.Contains(recording.PlaylistId))
            .Join(_database.Recordings, likedRecording => likedRecording.RecordingId, recording => recording.Id,
                (likedRecording, recording) => recording.Release.Artist.Id);
        return _database.Releases.AsNoTracking()
            .Where(release => listenedArtistIdentifiers.Contains(release.Artist.Id)
                || savedArtistIdentifiers.Contains(release.Artist.Id)
                || likedArtistIdentifiers.Contains(release.Artist.Id))
            .Where(release => !SavedAlbums(userId).Any(savedAlbum => savedAlbum.ReleaseId == release.Id));
    }

    private async Task<List<ReleaseResponse>> GetSavedAlbums(Guid userId)
    {
        var savedAlbums = await SavedAlbums(userId).Include(savedAlbum => savedAlbum.Release)
            .ThenInclude(release => release.Artist)
            .OrderByDescending(savedAlbum => savedAlbum.SavedAt).ThenBy(savedAlbum => savedAlbum.ReleaseId)
            .Take(RecommendationConstants.SectionItemLimit).ToListAsync();
        return savedAlbums.Select(savedAlbum => savedAlbum.Release.ToResponse(_imageService)).ToList();
    }

    private Task<List<ReleaseResponse>> GetArtistAlbums(Guid userId) => ArtistAlbums(userId)
        .Include(release => release.Artist)
        .OrderByDescending(release => release.DateAdded).ThenBy(release => release.Id)
        .Take(RecommendationConstants.SectionItemLimit).SelectResponses(_imageService);

    private Task<List<ReleaseResponse>> GetLatestAlbums() => _database.Releases.AsNoTracking()
        .Include(release => release.Artist)
        .OrderByDescending(release => release.DateAdded).ThenBy(release => release.Id)
        .Take(RecommendationConstants.SectionItemLimit).SelectResponses(_imageService);

    private sealed class RecentCollection
    {
        public Guid Identifier { get; init; }
        public string Type { get; init; } = string.Empty;
        public DateTime LastPlayedAt { get; init; }
    }
}

internal static class RecommendationQueryExtensions
{
    public static async Task<List<ReleaseResponse>> SelectResponses(this IQueryable<Release> releases, IImageService imageService) =>
        (await releases.ToListAsync()).Select(release => release.ToResponse(imageService)).ToList();
}
