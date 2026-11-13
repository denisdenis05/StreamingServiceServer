using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamingServiceServer.Business.Services.Recommendations;

namespace StreamingServiceServer.Background;

public sealed class RecommendationCacheJobService(IAudioRecommendationFeatures features, TimeProvider clock,
    ILogger<RecommendationCacheJobService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await features.GetAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Recommendation features could not be refreshed."); }
            await Task.Delay(RecommendationPolicy.CacheLifetime, clock, stoppingToken);
        }
    }
}
