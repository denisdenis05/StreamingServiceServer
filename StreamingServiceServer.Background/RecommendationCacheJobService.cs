using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.Business.Services.Recommendations;

namespace StreamingServiceServer.Background;

public sealed class RecommendationCacheJobService(IAudioRecommendationFeatures features, TimeProvider clock,
    ILogger<RecommendationCacheJobService> logger, IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IRecommendationPreviewCache>().DeleteExpiredAsync(stoppingToken);
                await features.GetAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Recommendation cache maintenance failed."); }
            await Task.Delay(RecommendationPolicy.CacheLifetime, clock, stoppingToken);
        }
    }
}
