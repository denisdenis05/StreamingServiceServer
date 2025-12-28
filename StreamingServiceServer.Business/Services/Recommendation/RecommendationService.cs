using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Business.Constants;

namespace StreamingServiceServer.Business.Services.Recommendation;

public class RecommendationService : IRecommendationService
{
    private readonly StreamingDbContext _dbContext;
    private readonly IImageService _imageService;

    public RecommendationService(StreamingDbContext dbContext, IImageService imageService)
    {
        _dbContext = dbContext;
        _imageService = imageService;
    }


    public async Task<List<RecommendationSection>> GetSections(Guid userId)
    {
        var sections = new List<RecommendationSection>
        {
            new RecommendationSection { Id = RecommendationConstants.Today, Title = "Recommended for today" },
            new RecommendationSection { Id = RecommendationConstants.Latest, Title = "Latest added releases" }
        };

        var hasRecentListens = await _dbContext.Listens
            .Where(l => l.UserId == userId)
            .Join(_dbContext.Recordings, l => l.RecordingId, r => r.Id, (l, r) => l)
            .AnyAsync();

        if (hasRecentListens)
        {
            sections.Add(new RecommendationSection { Id = RecommendationConstants.Recent, Title = "Recent Listens" });
        }

        sections.Add(new RecommendationSection { Id = RecommendationConstants.Picks, Title = "Best picks for you" });
        sections.Add(new RecommendationSection { Id = RecommendationConstants.Friends, Title = "Friends also listen to" });

        return sections;
    }

    public async Task<List<ReleaseResponse>> GetSection(string sectionId, Guid userId)
    {
        switch (sectionId)
        {
            case RecommendationConstants.Today:
                return await GetRecommendedForToday();
            case RecommendationConstants.Latest:
                return await GetLatestAddedReleases();
            case RecommendationConstants.Recent:
                return await GetRecentListens(userId);
            case RecommendationConstants.Picks:
                return await GetBestPicksForYou();
            case RecommendationConstants.Friends:
                return await GetFriendsAlsoListenTo();
            default:
                return new List<ReleaseResponse>();
        }
    }

    private async Task<List<ReleaseResponse>> GetRecommendedForToday()
    {
        return await GetRandomReleases(10);
    }

    private async Task<List<ReleaseResponse>> GetLatestAddedReleases()
    {
        var releases = await _dbContext.Releases
            .Include(r => r.Artist)
            .OrderByDescending(r => r.DateAdded)
            .Take(10)
            .ToListAsync();

        return releases.Select(r => r.ToResponse(_imageService)).ToList();
    }

    private async Task<List<ReleaseResponse>> GetBestPicksForYou()
    {
        return await GetRandomReleases(10);
    }

    private async Task<List<ReleaseResponse>> GetFriendsAlsoListenTo()
    {
        return await GetRandomReleases(10);
    }

    private async Task<List<ReleaseResponse>> GetRandomReleases(int count)
    {
        var releases = await _dbContext.Releases
            .Include(r => r.Artist)
            .OrderBy(r => Guid.NewGuid())
            .Take(count)
            .ToListAsync();

        return releases.Select(r => r.ToResponse(_imageService)).ToList();
    }

    private async Task<List<ReleaseResponse>> GetRecentListens(Guid userId)
    {
        var recentListens = await _dbContext.Listens
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.StartTime)
            .Take(20)
            .ToListAsync();

        var releaseIds = recentListens.Where(l => l.PlaylistId == null).Select(l => l.RecordingId).Distinct().ToList();
        var playlistIds = recentListens.Where(l => l.PlaylistId != null).Select(l => l.PlaylistId!.Value).Distinct().ToList();

        var releases = await _dbContext.Recordings
            .Where(r => releaseIds.Contains(r.Id))
            .Include(r => r.Release)
            .ThenInclude(rel => rel.Artist)
            .Select(r => r.Release)
            .Distinct()
            .Take(10)
            .ToListAsync();

        var playlists = await _dbContext.Playlists
            .Include(p => p.Owner)
            .Where(p => playlistIds.Contains(p.Id))
            .Take(10)
            .ToListAsync();

        var response = new List<ReleaseResponse>();
        
        response.AddRange(releases.Select(r => r.ToResponse(_imageService)));
        response.AddRange(playlists.Select(p => new ReleaseResponse
        {
            Id = p.Id,
            Title = p.Name,
            ArtistName = p.Owner.Username,
            Cover = p.Cover, 
            Type = "Playlist"
        }));

        return response.OrderBy(r => Guid.NewGuid()).Take(10).ToList(); // Shuffle mixed results
    }
}
