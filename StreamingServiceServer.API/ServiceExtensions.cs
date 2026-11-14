using Microsoft.AspNetCore.Identity;
using StreamingServiceServer.Business.Services.Artists;
using SocialMedia.Data.Models;
using StreamingServiceServer.Business.Services.Authentication;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.LastFm;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Business.Services.Recommendation;
using StreamingServiceServer.PreviewDownloader;
using StreamingServiceServer.Business.Services.SpotifyCatalog;

public static class ServiceExtensions
{
    public static void AddApplicationServices(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<IArtistCatalogProvider, MusicBrainzService>();
        services.AddScoped<ArtistCatalogService>();
        services.AddScoped<IRecordingCatalogQuery, RecordingCatalogQuery>();
        services.AddScoped<IExternalMusicSearchService, MusicBrainzService>();
        services.AddScoped<IMetadataService, MetadataService>();
        services.AddScoped<IStreamingService, StreamingService>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<ILastFmService, LastFmService>();
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<IPreviewDownloaderService, PreviewDownloaderService>();
        services.AddScoped<IImageService, ImageService>();
        services.AddScoped<IRecommendationService, RecommendationService>();
    }
}
