using Microsoft.Extensions.DependencyInjection;
using StreamingServiceDownloader.Services.ExternalMusicDownloader;
using StreamingServiceServer.Business.Services.MusicSearch;

public static class ServiceExtensions
{
    public static void AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IExternalMusicSearchService, MusicBrainzService>();
        services.AddScoped<IMetadataService, MetadataService>();
        services.AddScoped<IStreamingService, StreamingService>();
        services.AddScoped<IExternalMusicDownloader, ExternalMusicDownloader>();
    }
}
