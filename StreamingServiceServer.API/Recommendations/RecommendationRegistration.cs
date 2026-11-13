using StreamingServiceServer.Business.Services.Recommendations;
using StreamingServiceServer.Business.Services.Images.Playlists;

namespace StreamingServiceServer.API.Recommendations;

public static class RecommendationRegistration
{
    public static IServiceCollection AddAudioRecommendations(this IServiceCollection services)
    {
        services.AddSingleton<IAudioRecommendationFeatures, AudioRecommendationFeatures>();
        services.AddSingleton<RecommendationRanking>();
        services.AddSingleton<IRecommendationEntropy, SystemRecommendationEntropy>();
        services.AddSingleton<IRecommendationCursorCodec, RecommendationCursorCodec>();
        services.AddScoped<IAudioRecommendations, AudioRecommendations>();
        services.AddSingleton<IPlaylistCollageRenderer, PlaylistCollageRenderer>();
        services.AddScoped<IPlaylistArtworkService, PlaylistArtworkService>();
        services.AddScoped<PlaylistArtworkInterceptor>();
        services.AddSingleton<PlaylistArtworkQueue>();
        services.AddHostedService<StreamingServiceServer.Background.PlaylistArtworkJobService>();
        services.AddHostedService<StreamingServiceServer.Background.RecommendationCacheJobService>();
        return services;
    }
}
