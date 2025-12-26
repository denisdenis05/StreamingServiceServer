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


    public async Task<List<RecommendationSection>> GetSections()
    {
        return new List<RecommendationSection>
        {
            new RecommendationSection { Id = RecommendationConstants.Today, Title = "Recommended for today" },
            new RecommendationSection { Id = RecommendationConstants.Latest, Title = "Latest added releases" },
            new RecommendationSection { Id = RecommendationConstants.Picks, Title = "Best picks for you" },
            new RecommendationSection { Id = RecommendationConstants.Friends, Title = "Friends also listen to" }
        };
    }

    public async Task<List<ReleaseResponse>> GetSection(string sectionId)
    {
        switch (sectionId)
        {
            case RecommendationConstants.Today:
                return await GetRecommendedForToday();
            case RecommendationConstants.Latest:
                return await GetLatestAddedReleases();
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
}
