using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.Recommendation;

public interface IRecommendationService
{
    Task<List<RecommendationSection>> GetSections();
    Task<List<ReleaseResponse>> GetSection(string sectionId);
}

public class RecommendationSection
{
    public string Id { get; set; }
    public string Title { get; set; }
}
