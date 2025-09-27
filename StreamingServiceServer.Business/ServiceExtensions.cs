using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SocialMedia.Data.Models;
using StreamingServiceServer.Business.Services.Authentication;
using StreamingServiceServer.Business.Services.LastFm;
using StreamingServiceServer.Business.Services.MusicSearch;

public static class ServiceExtensions
{
    public static void AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IExternalMusicSearchService, MusicBrainzService>();
        services.AddScoped<IMetadataService, MetadataService>();
        services.AddScoped<IStreamingService, StreamingService>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<ILastFmService, LastFmService>();
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
    }
}
