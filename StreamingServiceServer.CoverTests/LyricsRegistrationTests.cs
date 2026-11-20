using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StreamingServiceServer.API.Lyrics;
using StreamingServiceServer.Background;
using StreamingServiceServer.Business.Lyrics;
using StreamingServiceServer.Data;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class LyricsRegistrationTests
{
    private const string SettingSeparator = ":";

    [Fact]
    public void DefaultRegistrationResolvesStoresWorkerAndAnonymousClientWithoutNetworkAccess()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new TestClock());
        services.AddDbContext<StreamingDbContext>(options => options.UseNpgsql(LyricsTestData.DesignConnection));
        services.AddLyricsDownloads(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        Assert.IsType<LyricsJobStore>(scope.ServiceProvider.GetRequiredService<ILyricsJobStore>());
        Assert.IsType<LyricsCatalog>(scope.ServiceProvider.GetRequiredService<ILyricsCatalog>());
        Assert.IsType<LrclibLyricsProvider>(scope.ServiceProvider.GetRequiredService<ILyricsProvider>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<LyricsReader>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<LyricsTrackMatchStore>());
        Assert.Single(provider.GetServices<IHostedService>(), service => service is LyricsDownloadJobService);
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(ILyricsProvider));
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ILyricsProvider));
        Assert.Equal(LyricsProtocol.BaseAddress, client.BaseAddress!.AbsoluteUri);
        Assert.Equal(LyricsProtocol.UserAgent, client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.False(client.DefaultRequestHeaders.Contains(LyricsTestData.ProviderKeyHeader));
    }

    [Theory]
    [InlineData(nameof(LyricsOptions.DiscoveryBatchSize), LyricsTestData.InvalidBatch)]
    [InlineData(nameof(LyricsOptions.DiscoveryInterval), LyricsTestData.ShortInterval)]
    [InlineData(nameof(LyricsOptions.PollInterval), LyricsTestData.ShortInterval)]
    [InlineData(nameof(LyricsOptions.RequestInterval), LyricsTestData.ShortInterval)]
    [InlineData(nameof(LyricsOptions.OperationTimeout), LyricsTestData.ShortInterval)]
    [InlineData(nameof(LyricsOptions.LeaseLifetime), LyricsTestData.UnsafeLease)]
    [InlineData(nameof(LyricsOptions.MetadataRecheckInterval), LyricsTestData.ShortInterval)]
    [InlineData(nameof(LyricsOptions.NotFoundRetryInterval), LyricsTestData.ShortInterval)]
    [InlineData(nameof(LyricsOptions.InitialRetryDelay), LyricsTestData.ShortInterval)]
    [InlineData(nameof(LyricsOptions.MaximumRetryDelay), LyricsTestData.ShortInterval)]
    public void InvalidConfigurationIsRejectedBeforeWorkerStarts(string setting, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [LyricsOptions.Section + SettingSeparator + setting] = value }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLyricsDownloads(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<LyricsOptions>>().Value);
    }
}
