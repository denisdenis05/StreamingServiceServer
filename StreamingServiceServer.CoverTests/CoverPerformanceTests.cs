using System.Buffers.Binary;
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

public sealed class CoverPerformanceTests
{
    [Fact]
    public async Task SharedAssetIntegrityIsHashedOncePerInterval()
    {
        var store = new VerificationStore();
        var clock = new TestClock();
        var options = new CoverAssetOptions();
        var verification = new CoverAssetVerification(store, Options.Create(options), clock);
        for (var recording = 0; recording < 100; recording++) Assert.True(await verification.CheckAsync(store.Assets, CancellationToken.None));
        Assert.Equal(1, store.HashChecks);
        Assert.Equal(100, store.PresenceChecks);
        clock.Advance(options.IntegrityInterval);
        Assert.True(await verification.CheckAsync(store.Assets, CancellationToken.None));
        Assert.Equal(2, store.HashChecks);
    }

    [Fact]
    public async Task MissingFilesInvalidatePreviousSuccessfulVerification()
    {
        var store = new VerificationStore();
        var verification = new CoverAssetVerification(store, Options.Create(new CoverAssetOptions()), TimeProvider.System);
        Assert.True(await verification.CheckAsync(store.Assets, CancellationToken.None));
        store.Present = false;
        Assert.False(await verification.CheckAsync(store.Assets, CancellationToken.None));
        store.Present = true;
        Assert.True(await verification.CheckAsync(store.Assets, CancellationToken.None));
        Assert.Equal(2, store.HashChecks);
    }

    [Fact]
    public async Task VerificationMemoryIsBoundedAndEvictedAssetsAreVerifiedAgain()
    {
        var store = new VerificationStore();
        var verification = new CoverAssetVerification(store, Options.Create(new CoverAssetOptions { VerificationCacheEntries = 1 }), TimeProvider.System);
        await verification.CheckAsync(store.Assets, CancellationToken.None);
        await verification.CheckAsync(store.Assets with { Identity = new string('b', 64) }, CancellationToken.None);
        await verification.CheckAsync(store.Assets, CancellationToken.None);
        Assert.Equal(3, store.HashChecks);
    }

    [Fact]
    public async Task PersistedIntegrityDeadlineSurvivesMemoryCacheEviction()
    {
        var store = new VerificationStore();
        var clock = new TestClock();
        var options = new CoverAssetOptions { VerificationCacheEntries = 1 };
        var verification = new CoverAssetVerification(store, Options.Create(options), clock);
        await verification.CheckAsync(store.Assets, CancellationToken.None);
        await verification.CheckAsync(store.Assets with { Identity = new string('b', 64) }, CancellationToken.None);
        await verification.CheckAsync(store.Assets, CancellationToken.None, clock.GetUtcNow() + options.IntegrityInterval);
        Assert.Equal(2, store.HashChecks);
        Assert.Equal(3, store.PresenceChecks);
    }

    [Fact]
    public async Task HugeEncodedDimensionsAreRejectedBeforeLaunchingRenderer()
    {
        var path = Path.GetTempFileName();
        try
        {
            var header = new byte[24];
            new byte[] {137,80,78,71,13,10,26,10}.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 100000);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 100000);
            await File.WriteAllBytesAsync(path, header);
            var renderer = new FfmpegCoverImageRenderer(Options.Create(new CoverAssetOptions { RendererExecutable = "/does-not-exist" }));
            await Assert.ThrowsAsync<IOException>(() => renderer.RenderAsync(path, path + ".jpg", 250, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task AvailableWorkerSlotStartsNextJobWhileAnotherDownloadIsBlocked()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        fixture.Assets.Options.ParallelJobs = 2;
        fixture.Assets.Options.BatchSize = 3;
        for (var releaseIndex = 0; releaseIndex < 3; releaseIndex++)
        {
            var identifier = await fixture.AddReleaseAsync();
            using var scope = fixture.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var release = await database.Releases.SingleAsync(release => release.Id == identifier);
            release.Cover = $"https://images.invalid/{releaseIndex}.jpg";
            await database.SaveChangesAsync();
        }
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        fixture.Assets.Handler.Wait = async () =>
        {
            var number = Interlocked.Increment(ref requests);
            if (number == 1) await releaseFirst.Task;
            if (number == 3) thirdStarted.TrySetResult();
        };
        var processing = fixture.Worker.ProcessDueAsync(CancellationToken.None);
        try { await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { releaseFirst.TrySetResult(); await processing; }
        Assert.Equal(3, requests);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task InvalidMaintenanceManifestDoesNotBlockPendingDownload()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            database.CoverAssetJobs.Add(new CoverAssetJob { EntityKind = CoverEntityKind.Recording,
                EntityIdentifier = Guid.NewGuid(), AssetManifest = "{\"Identity\":null}", CompletedAt = fixture.Clock.GetUtcNow(),
                NextAttemptAt = fixture.Clock.GetUtcNow(), Version = Guid.NewGuid() });
            await database.SaveChangesAsync();
        }
        var maintenance = new CoverAssetMaintenanceService(fixture.Services.GetRequiredService<IServiceScopeFactory>(), fixture.Assets.Store,
            Options.Create(fixture.Assets.Options), fixture.Clock, NullLogger<CoverAssetMaintenanceService>.Instance);
        await Task.WhenAll(maintenance.RunOnceAsync(CancellationToken.None), fixture.Worker.ProcessDueAsync(CancellationToken.None));
        using var verification = fixture.Services.CreateScope();
        Assert.NotEmpty((await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().Releases.SingleAsync()).LocalCover);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task UserChangesRunFirstAndOldBackfillReceivesEveryTenthAdmission()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        fixture.Assets.Options.BatchSize = 1;
        fixture.Assets.Options.ParallelJobs = 1;
        var backfillIdentifier = await fixture.AddReleaseAsync();
        for (var releaseIndex = 0; releaseIndex < 10; releaseIndex++) await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            await database.CoverAssetJobs.Where(job => job.EntityIdentifier == backfillIdentifier)
                .ExecuteUpdateAsync(update => update.SetProperty(job => job.Priority, CoverJobPriority.Backfill)
                    .SetProperty(job => job.EnqueuedAt, fixture.Clock.GetUtcNow() - TimeSpan.FromDays(1)));
        }
        var worker = fixture.NewWorker();
        for (var admission = 0; admission < 9; admission++) await worker.ProcessDueAsync(CancellationToken.None);
        using (var scope = fixture.Services.CreateScope())
            Assert.Null((await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.SingleAsync(
                job => job.EntityIdentifier == backfillIdentifier)).CompletedAt);
        await worker.ProcessDueAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope();
        Assert.NotNull((await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.SingleAsync(
            job => job.EntityIdentifier == backfillIdentifier)).CompletedAt);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task PersistedSharedIntegrityScheduleSurvivesServiceRecreation()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        fixture.Assets.Options.SourceLifetime = TimeSpan.FromDays(30);
        await fixture.AddReleaseAsync();
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        var counted = new CountingStore(fixture.Assets.Store);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        CoverAssetMaintenanceService NewMaintenance() => new(fixture.Services.GetRequiredService<IServiceScopeFactory>(), counted,
            Options.Create(fixture.Assets.Options), fixture.Clock, NullLogger<CoverAssetMaintenanceService>.Instance);
        await NewMaintenance().RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, counted.HashChecks);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await NewMaintenance().RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, counted.HashChecks);
        fixture.Clock.Advance(fixture.Assets.Options.IntegrityInterval);
        await NewMaintenance().RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, counted.HashChecks);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task BackfillCursorSurvivesRecreationAndBoundsEachMetadataPage()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        fixture.Assets.Options.MaintenanceBatchSize = 1;
        await fixture.AddReleaseAsync();
        await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.ExecuteDeleteAsync();
        CoverAssetMaintenanceService NewMaintenance() => new(fixture.Services.GetRequiredService<IServiceScopeFactory>(), fixture.Assets.Store,
            Options.Create(fixture.Assets.Options), fixture.Clock, NullLogger<CoverAssetMaintenanceService>.Instance);
        await NewMaintenance().RunOnceAsync(CancellationToken.None);
        Guid firstIdentifier;
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            firstIdentifier = (await database.CoverAssetJobs.SingleAsync()).EntityIdentifier;
            Assert.Equal(firstIdentifier, (await database.CoverBackfillStates.SingleAsync(state => state.EntityKind == CoverEntityKind.Release)).PositionIdentifier);
        }
        await NewMaintenance().RunOnceAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope();
        var current = verification.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.Equal(2, await current.CoverAssetJobs.CountAsync());
        Assert.NotEqual(firstIdentifier, (await current.CoverBackfillStates.SingleAsync(state => state.EntityKind == CoverEntityKind.Release)).PositionIdentifier);
    }

    private sealed class CountingStore(ICoverAssetStore inner) : ICoverAssetStore
    {
        public int HashChecks { get; private set; }
        public Task<bool> IsValidAsync(CoverAssetSet assets, CancellationToken cancellationToken)
        { HashChecks++; return inner.IsValidAsync(assets, cancellationToken); }
        public Task<bool> IsPresentAsync(CoverAssetSet assets, CancellationToken cancellationToken) => inner.IsPresentAsync(assets, cancellationToken);
        public Task<CoverAssetSet> GetOrCreateAsync(string sourceAddress, CancellationToken cancellationToken) => inner.GetOrCreateAsync(sourceAddress,cancellationToken);
        public Task<CoverAssetSet> ImportAsync(Stream input,CancellationToken cancellationToken) => inner.ImportAsync(input,cancellationToken);
        public Task<CoverAssetSet> RestoreAsync(CoverAssetSet assets,CancellationToken cancellationToken) => inner.RestoreAsync(assets,cancellationToken);
        public Task PruneAsync(IReadOnlySet<string> identities,CancellationToken cancellationToken) => inner.PruneAsync(identities,cancellationToken);
        public string NormalizeLocalAddress(string address) => inner.NormalizeLocalAddress(address);
        public string PublicAddress(CoverAssetVariant variant) => inner.PublicAddress(variant);
    }

    private sealed class VerificationStore : ICoverAssetStore
    {
        private static readonly CoverAssetVariant Variant = new("image.jpg", 1, new string('a', 64));
        public CoverAssetSet Assets { get; } = new(new string('a',64),new string('a',64),Variant,Variant,Variant);
        public int HashChecks { get; private set; }
        public int PresenceChecks { get; private set; }
        public bool Present { get; set; } = true;
        public Task<bool> IsPresentAsync(CoverAssetSet assets, CancellationToken cancellationToken) { PresenceChecks++; return Task.FromResult(Present); }
        public Task<bool> IsValidAsync(CoverAssetSet assets, CancellationToken cancellationToken) { HashChecks++; return Task.FromResult(true); }
        public Task<CoverAssetSet> GetOrCreateAsync(string sourceAddress, CancellationToken cancellationToken) => Task.FromResult(Assets);
        public Task<CoverAssetSet> ImportAsync(Stream input, CancellationToken cancellationToken) => Task.FromResult(Assets);
        public Task<CoverAssetSet> RestoreAsync(CoverAssetSet assets, CancellationToken cancellationToken) => Task.FromResult(assets);
        public Task PruneAsync(IReadOnlySet<string> retainedIdentities, CancellationToken cancellationToken) => Task.CompletedTask;
        public string NormalizeLocalAddress(string address) => address;
        public string PublicAddress(CoverAssetVariant variant) => variant.Filename;
    }
}
