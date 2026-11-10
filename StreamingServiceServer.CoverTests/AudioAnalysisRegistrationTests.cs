using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StreamingServiceServer.API.AudioAnalysis;
using StreamingServiceServer.Background;
using StreamingServiceServer.Business.AudioAnalysis;
using StreamingServiceServer.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class AudioAnalysisRegistrationTests
{
    private const string KeyDirectorySetting = "AudioAnalysis:KeyDirectory";
    private const string TemporaryFolderPrefix = "audio-analysis-registration-";
    private const string DesignOnlyConnection = "Host=127.0.0.1;Port=1;Database=audio_analysis_registration_only;Username=design_only";
    private const string TestSecret = "registration-test-token";
    private const string ApplicationName = "AudioAnalysisRegistrationTests";

    [Fact]
    public void DependencyRegistrationResolvesScopedStoresClientSourceAndWorkerWithoutConnectingToDatabase()
    {
        var keyDirectory = Path.Combine(Path.GetTempPath(), TemporaryFolderPrefix + Guid.NewGuid().ToString());
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [KeyDirectorySetting] = keyDirectory }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddDbContext<StreamingDbContext>(options => options.UseNpgsql(DesignOnlyConnection));
            services.AddLocalAudioAnalysis(configuration, new TestEnvironment());
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            using var scope = provider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAudioAnalysisSettingsStore>();
            Assert.Same(settings, scope.ServiceProvider.GetRequiredService<IAudioAnalysisAdministrationStore>());
            Assert.IsType<AudioAnalysisSettingsStore>(settings);
            Assert.IsType<AudioAnalysisClient>(scope.ServiceProvider.GetRequiredService<IAudioAnalysisClient>());
            Assert.IsType<LocalAudioAnalysisSource>(scope.ServiceProvider.GetRequiredService<IAudioAnalysisSource>());
            Assert.IsType<AudioAnalysisJobStore>(scope.ServiceProvider.GetRequiredService<IAudioAnalysisJobStore>());
            Assert.Single(provider.GetServices<IHostedService>(), service => service is AudioAnalysisJobService);
            var secrets = provider.GetRequiredService<IAudioAnalysisSecretStore>();
            Assert.Equal(TestSecret, secrets.Unprotect(secrets.Protect(TestSecret)));
            Assert.NotEmpty(Directory.EnumerateFiles(keyDirectory));
            var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IAudioAnalysisClient));
            while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
            Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
        }
        finally
        {
            if (Directory.Exists(keyDirectory)) Directory.Delete(keyDirectory, recursive: true);
        }
    }

    private const string WorkerSettingsPrefix = AudioAnalysisJobOptions.ConfigurationSection + ":";
    private const string InvalidInterval = "00:00:00";
    private const string ShortLease = "00:01:00";
    private const string InvalidBatchSize = "0";
    private const string CustomBatchSize = "16";
    private const int ExpectedBatchSize = 16;

    [Theory]
    [InlineData(nameof(AudioAnalysisJobOptions.PollInterval), InvalidInterval)]
    [InlineData(nameof(AudioAnalysisJobOptions.SettingsCheckInterval), InvalidInterval)]
    [InlineData(nameof(AudioAnalysisJobOptions.OperationTimeout), InvalidInterval)]
    [InlineData(nameof(AudioAnalysisJobOptions.LeaseLifetime), ShortLease)]
    [InlineData(nameof(AudioAnalysisJobOptions.ProviderFailureCooldown), InvalidInterval)]
    [InlineData(nameof(AudioAnalysisJobOptions.InitialRetryDelay), InvalidInterval)]
    [InlineData(nameof(AudioAnalysisJobOptions.MaximumRetryDelay), InvalidInterval)]
    [InlineData(nameof(AudioAnalysisJobOptions.DiscoveryBatchSize), InvalidBatchSize)]
    public void WorkerConfigurationRejectsInvalidTimingAndLimits(string setting, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [WorkerSettingsPrefix + setting] = value }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalAudioAnalysis(configuration, new TestEnvironment());
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AudioAnalysisJobOptions>>().Value);
    }

    [Fact]
    public void WorkerConfigurationBindsValidHostSettings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [WorkerSettingsPrefix + nameof(AudioAnalysisJobOptions.DiscoveryBatchSize)] = CustomBatchSize }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalAudioAnalysis(configuration, new TestEnvironment());
        using var provider = services.BuildServiceProvider();
        Assert.Equal(ExpectedBatchSize, provider.GetRequiredService<IOptions<AudioAnalysisJobOptions>>().Value.DiscoveryBatchSize);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = AudioAnalysisRegistrationTests.ApplicationName;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
