using Microsoft.AspNetCore.DataProtection;
using StreamingServiceServer.Background;
using StreamingServiceServer.Business.AudioAnalysis;

namespace StreamingServiceServer.API.AudioAnalysis;

public static class AudioAnalysisRegistration
{
    private const string KeyDirectoryConfiguration = "AudioAnalysis:KeyDirectory";
    private const string DefaultKeyDirectory = ".audio-analysis-keys";
    private const string ApplicationName = "StreamingServiceServer";
    private const string InvalidWorkerOptions = "Audio analysis worker limits and intervals must be positive, with a lease longer than the operation timeout.";

    public static IServiceCollection AddLocalAudioAnalysis(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        var keyDirectory = configuration[KeyDirectoryConfiguration]
            ?? Path.Combine(environment.ContentRootPath, DefaultKeyDirectory);
        services.AddDataProtection().SetApplicationName(ApplicationName).PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        services.AddSingleton<IAudioAnalysisSecretStore, AudioAnalysisSecretStore>();
        services.AddScoped<AudioAnalysisSettingsStore>();
        services.AddScoped<IAudioAnalysisSettingsStore>(provider => provider.GetRequiredService<AudioAnalysisSettingsStore>());
        services.AddScoped<IAudioAnalysisAdministrationStore>(provider => provider.GetRequiredService<AudioAnalysisSettingsStore>());
        services.AddScoped<IAudioAnalysisSource, LocalAudioAnalysisSource>();
        services.AddScoped<IAudioAnalysisJobStore, AudioAnalysisJobStore>();
        services.AddHttpClient<IAudioAnalysisClient, AudioAnalysisClient>(client => client.Timeout = AudioAnalysisProtocol.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddOptions<AudioAnalysisJobOptions>().Bind(configuration.GetSection(AudioAnalysisJobOptions.ConfigurationSection))
            .Validate(options => options.DiscoveryBatchSize > 0 && options.PollInterval > TimeSpan.Zero
                && options.SettingsCheckInterval > TimeSpan.Zero && options.OperationTimeout > TimeSpan.Zero
                && options.LeaseLifetime > options.OperationTimeout && options.ProviderFailureCooldown > TimeSpan.Zero
                && options.InitialRetryDelay > TimeSpan.Zero && options.MaximumRetryDelay >= options.InitialRetryDelay,
                InvalidWorkerOptions).ValidateOnStart();
        services.AddHostedService<AudioAnalysisJobService>();
        return services;
    }
}
