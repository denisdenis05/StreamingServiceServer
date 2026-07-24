using StreamingServiceServer.Business.Models;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.Recommendation;

public interface IRecommendationService
{
    Task<List<RecommendationSection>> GetSections(Guid userId);
    Task<List<ReleaseResponse>> GetSection(string sectionId, Guid userId);
}

