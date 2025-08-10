using Microsoft.Extensions.DependencyInjection;
using StreamingServiceDownloader.Helpers;

public static class HelpersExtensions
{
    public static void RegisterHelpers(this IServiceCollection services)
    {
        services.AddScoped<ITorrentHelper, QBittorrentHelper>();
    }
}
