using StreamingServiceServer.Background;
using StreamingServiceServer.Business.Lyrics;

namespace StreamingServiceServer.API.Lyrics;

public static class LyricsRegistration
{
    private const string InvalidOptions = "Lyrics limits must be positive, with a lease longer than the operation timeout and requests at least one second apart.";

    public static IServiceCollection AddLyricsDownloads(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LyricsOptions>().Bind(configuration.GetSection(LyricsOptions.Section))
            .Validate(options => options.DiscoveryBatchSize >= LyricsProtocol.MinimumDiscoveryBatchSize
                && options.DiscoveryInterval > TimeSpan.Zero && options.PollInterval > TimeSpan.Zero && options.RequestInterval >= options.MinimumRequestInterval
                && options.OperationTimeout > TimeSpan.Zero && options.LeaseLifetime > options.OperationTimeout
                && options.MetadataRecheckInterval > TimeSpan.Zero && options.NotFoundRetryInterval > TimeSpan.Zero
                && options.InitialRetryDelay > TimeSpan.Zero && options.MaximumRetryDelay >= options.InitialRetryDelay, InvalidOptions).ValidateOnStart();
        services.AddSingleton<ILyricsIdentifiers, LyricsIdentifiers>();
        services.AddScoped<ILyricsCatalog, LyricsCatalog>();
        services.AddScoped<ILyricsJobStore, LyricsJobStore>();
        services.AddScoped<LyricsReader>();
        services.AddScoped<LyricsTrackMatchStore>();
        services.AddTransient<ILyricsRequestDelay, LyricsRequestDelay>();
        services.AddHttpClient<ILyricsProvider, LrclibLyricsProvider>(client =>
        {
            client.BaseAddress = new Uri(LyricsProtocol.BaseAddress);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(LyricsProtocol.UserAgent);
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHostedService<LyricsDownloadJobService>();
        return services;
    }

    private sealed class LyricsIdentifiers : ILyricsIdentifiers
    {
        public Guid Create() => Guid.NewGuid();
    }
}
