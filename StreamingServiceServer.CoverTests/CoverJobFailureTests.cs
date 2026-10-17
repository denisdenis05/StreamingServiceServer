using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Background;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

[Trait("Category", "Postgres")]
public sealed class CoverJobFailureTests
{
    [PostgresFact]
    public async Task PermanentCandidateFailuresSuspendUntilSourceChanges()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        var store = new FailingStore((sourceAddress, cancellationToken) => Task.FromException<CoverAssetSet>(new CoverSourceFailure("not found", true)));
        await Worker(fixture, store).ProcessDueAsync(CancellationToken.None);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var job = await database.CoverAssetJobs.SingleAsync(); Assert.True(job.Suspended); Assert.Null(job.LeaseToken);
        var release = await database.Releases.SingleAsync(); release.SmallCover = "https://images.invalid/fallback.jpg";
        await database.SaveChangesAsync(); Assert.False(job.Suspended); Assert.Equal(0, job.Attempts);
    }
    [PostgresFact]
    public async Task TransientCandidateKeepsRetryEnabledAndHonorsProviderDelay()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        var providerDelay = TimeSpan.FromMinutes(10);
        var store = new FailingStore((sourceAddress, cancellationToken) => Task.FromException<CoverAssetSet>(new CoverSourceFailure("rate limited", false, providerDelay)));
        await Worker(fixture, store).ProcessDueAsync(CancellationToken.None);
        using var scope = fixture.Services.CreateScope(); var job = await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.SingleAsync();
        Assert.False(job.Suspended); Assert.Equal(fixture.Clock.GetUtcNow() + providerDelay, job.NextAttemptAt);
        Assert.Null(job.LeaseToken); Assert.Equal(1, job.Attempts);
    }
    [PostgresFact]
    public async Task WholeJobDeadlineReleasesLeaseAndPersistsRetry()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        fixture.Assets.Options.OperationTimeout = TimeSpan.FromMilliseconds(30);
        var store = new FailingStore(async (sourceAddress, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable download completion.");
        });
        await Worker(fixture, store).ProcessDueAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        using var scope = fixture.Services.CreateScope(); var job = await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.SingleAsync();
        Assert.False(job.Suspended); Assert.Null(job.LeaseToken); Assert.Null(job.LeaseUntil);
        Assert.Equal(1, job.Attempts); Assert.True(job.NextAttemptAt > fixture.Clock.GetUtcNow());
    }
    private static CoverAssetJobService Worker(DatabaseFixture fixture, ICoverAssetStore store) => new(
        fixture.Services.GetRequiredService<IServiceScopeFactory>(), store, Options.Create(fixture.Assets.Options), fixture.Clock,
        NullLogger<CoverAssetJobService>.Instance);

    private sealed class FailingStore(Func<string, CancellationToken, Task<CoverAssetSet>> download) : ICoverAssetStore
    {
        public Task<CoverAssetSet> GetOrCreateAsync(string sourceAddress, CancellationToken cancellationToken) => download(sourceAddress, cancellationToken);
        public Task<CoverAssetSet> ImportAsync(Stream input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CoverAssetSet> RestoreAsync(CoverAssetSet assets, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsValidAsync(CoverAssetSet assets, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PruneAsync(IReadOnlySet<string> retainedIdentities, CancellationToken cancellationToken) => throw new NotSupportedException();
        public string NormalizeLocalAddress(string address) => address;
        public string PublicAddress(CoverAssetVariant variant) => throw new NotSupportedException();
    }
}
