using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamingServiceServer.Background;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

[Trait("Category", "Postgres")]
public sealed class CoverMetadataRefreshTests
{
    [PostgresFact]
    public async Task RefreshRequestQueuesDurableWorkWithoutProviderCallsAndCoalescesRepeatedRequests()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.AddReleaseAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var metadata = new MetadataService(database, null!, null!, null!);
        await metadata.RefreshAllAlbumCovers();
        var first = await metadata.GetCoverMetadataRefresh();
        await metadata.RefreshAllAlbumCovers();
        var second = await metadata.GetCoverMetadataRefresh();
        Assert.True(second!.Pending);
        Assert.Equal(first!.Version, second.Version);
        Assert.Null(second.Cursor);
        Assert.Equal(0, second.CompletedReleases);
    }

    [PostgresFact]
    public async Task RefreshWorkerResumesFromCommittedReleaseCheckpointAfterRecreation()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.AddReleaseAsync();
        await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope())
            await new MetadataService(scope.ServiceProvider.GetRequiredService<StreamingDbContext>(), null!, null!, null!).RefreshAllAlbumCovers();
        var provider = DispatchProxy.Create<IExternalMusicSearchService, MetadataProxy>();
        var scopes = new MetadataScopeFactory(fixture.Services.GetRequiredService<IServiceScopeFactory>(), provider);
        var worker = new CoverMetadataRefreshService(scopes, fixture.Clock, NullLogger<CoverMetadataRefreshService>.Instance);
        Assert.True(await worker.ProcessOneAsync(CancellationToken.None));
        worker = new CoverMetadataRefreshService(scopes, fixture.Clock, NullLogger<CoverMetadataRefreshService>.Instance);
        Assert.True(await worker.ProcessOneAsync(CancellationToken.None));
        Assert.False(await worker.ProcessOneAsync(CancellationToken.None));
        using var verification = fixture.Services.CreateScope();
        var database = verification.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var progress = await database.CoverMetadataRefreshes.SingleAsync();
        Assert.False(progress.Pending);
        Assert.Equal(2, progress.CompletedReleases);
        Assert.Equal(2, ((MetadataProxy)(object)provider).Requests);
        Assert.All(await database.Releases.ToListAsync(), release => Assert.StartsWith("https://coverartarchive.org/release/", release.Cover));
    }

    [PostgresFact]
    public async Task CancelledRefreshDoesNotApplyAnInFlightProviderResult()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope())
            await new MetadataService(scope.ServiceProvider.GetRequiredService<StreamingDbContext>(), null!, null!, null!).RefreshAllAlbumCovers();
        var provider = DispatchProxy.Create<IExternalMusicSearchService, MetadataProxy>();
        var pending = new TaskCompletionSource<ICollection<ReleaseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((MetadataProxy)(object)provider).Response = () => { started.TrySetResult(); return pending.Task; };
        var worker = new CoverMetadataRefreshService(new MetadataScopeFactory(fixture.Services.GetRequiredService<IServiceScopeFactory>(), provider),
            fixture.Clock, NullLogger<CoverMetadataRefreshService>.Instance);
        var processing = worker.ProcessOneAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var scope = fixture.Services.CreateScope())
            await new MetadataService(scope.ServiceProvider.GetRequiredService<StreamingDbContext>(), null!, null!, null!).CancelCoverMetadataRefresh();
        pending.SetResult(Array.Empty<ReleaseDto>());
        Assert.False(await processing);
        using var verification = fixture.Services.CreateScope();
        Assert.Equal(AssetFixture.Source, (await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().Releases.SingleAsync()).Cover);
    }

    [PostgresFact]
    public async Task ProviderFailureRetainsItsCheckpointAndSuccessfulRetryClearsFailure()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope())
            await new MetadataService(scope.ServiceProvider.GetRequiredService<StreamingDbContext>(), null!, null!, null!).RefreshAllAlbumCovers();
        var provider = DispatchProxy.Create<IExternalMusicSearchService, MetadataProxy>();
        var proxy = (MetadataProxy)(object)provider;
        proxy.Response = () => Task.FromException<ICollection<ReleaseDto>>(new HttpRequestException("fixture provider failure"));
        var worker = new CoverMetadataRefreshService(new MetadataScopeFactory(fixture.Services.GetRequiredService<IServiceScopeFactory>(), provider),
            fixture.Clock, NullLogger<CoverMetadataRefreshService>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(() => worker.ProcessOneAsync(CancellationToken.None));
        using (var scope = fixture.Services.CreateScope())
        {
            var progress = await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverMetadataRefreshes.SingleAsync();
            Assert.Null(progress.Cursor);
            Assert.Equal(0, progress.CompletedReleases);
            Assert.Equal(nameof(HttpRequestException), progress.LastFailure);
        }
        proxy.Response = () => Task.FromResult<ICollection<ReleaseDto>>(Array.Empty<ReleaseDto>());
        Assert.True(await worker.ProcessOneAsync(CancellationToken.None));
        using var verification = fixture.Services.CreateScope();
        Assert.Empty((await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverMetadataRefreshes.SingleAsync()).LastFailure);
    }

    [PostgresFact]
    public async Task MetadataRefreshInvalidatesAnInFlightInheritedRecordingJobBeforeOldCompletion()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var releaseIdentifier = await fixture.AddReleaseAsync();
        Guid recordingIdentifier;
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var recording = new Recording { Id = Guid.NewGuid(), Title = "inherited recording", Cover = AssetFixture.Source,
                Release = await database.Releases.SingleAsync() };
            recordingIdentifier = recording.Id; database.Recordings.Add(recording); await database.SaveChangesAsync();
            await database.CoverAssetJobs.Where(job => job.EntityKind == CoverEntityKind.Release).ExecuteDeleteAsync();
            await new MetadataService(database, null!, null!, null!).RefreshAllAlbumCovers();
        }
        fixture.Assets.Options.BatchSize = 1;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Assets.Handler.Wait = async () => { started.TrySetResult(); await finish.Task; };
        var downloading = fixture.Worker.ProcessDueAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Guid originalVersion;
        using (var scope = fixture.Services.CreateScope())
            originalVersion = (await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs
                .SingleAsync(job => job.EntityKind == CoverEntityKind.Recording)).Version;
        var provider = DispatchProxy.Create<IExternalMusicSearchService, MetadataProxy>();
        var worker = new CoverMetadataRefreshService(new MetadataScopeFactory(fixture.Services.GetRequiredService<IServiceScopeFactory>(), provider),
            fixture.Clock, NullLogger<CoverMetadataRefreshService>.Instance);
        await worker.ProcessOneAsync(CancellationToken.None);
        finish.SetResult(); await downloading;
        using var verification = fixture.Services.CreateScope();
        var current = verification.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var updatedRecording = await current.Recordings.SingleAsync(recording => recording.Id == recordingIdentifier);
        var updatedJob = await current.CoverAssetJobs.SingleAsync(job => job.EntityKind == CoverEntityKind.Recording);
        Assert.NotEqual(originalVersion, updatedJob.Version); Assert.Null(updatedJob.CompletedAt); Assert.Null(updatedJob.LeaseToken);
        Assert.Empty(updatedRecording.LocalCover); Assert.Empty(updatedRecording.LocalSmallCover);
        Assert.Equal(CoverSourceSet.Capture(updatedRecording.Cover, updatedRecording.SmallCover, updatedRecording.VerySmallCover).Revision,
            updatedJob.SourceRevision);
        Assert.Contains(releaseIdentifier.ToString(), updatedRecording.Cover);
    }

    [PostgresFact]
    public async Task MetadataRefreshLeavesCustomRecordingSourcesAndJobVersionsUnchanged()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        var customSource = "https://custom.invalid/recording.jpg";
        Guid customIdentifier; Guid originalVersion;
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var custom = new Recording { Id = Guid.NewGuid(), Title = "custom recording", Cover = customSource,
                LocalCover = "custom-local", Release = await database.Releases.SingleAsync() };
            customIdentifier = custom.Id; database.Recordings.Add(custom); await database.SaveChangesAsync();
            originalVersion = (await database.CoverAssetJobs.SingleAsync(job => job.EntityKind == CoverEntityKind.Recording)).Version;
            await new MetadataService(database, null!, null!, null!).RefreshAllAlbumCovers();
        }
        var provider = DispatchProxy.Create<IExternalMusicSearchService, MetadataProxy>();
        var worker = new CoverMetadataRefreshService(new MetadataScopeFactory(fixture.Services.GetRequiredService<IServiceScopeFactory>(), provider),
            fixture.Clock, NullLogger<CoverMetadataRefreshService>.Instance);
        await worker.ProcessOneAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope(); var current = verification.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var recording = await current.Recordings.SingleAsync(recording => recording.Id == customIdentifier);
        var job = await current.CoverAssetJobs.SingleAsync(candidate => candidate.EntityKind == CoverEntityKind.Recording);
        Assert.Equal(customSource, recording.Cover); Assert.Equal("custom-local", recording.LocalCover);
        Assert.Equal(originalVersion, job.Version);
    }

    [PostgresFact]
    public async Task ReleaseRowLockPreservesAnAdminSourceChangeWhileInheritedRecordingUpdateWaits()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); var releaseIdentifier = await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            database.Recordings.Add(new Recording { Id = Guid.NewGuid(), Title = "locked recording", Cover = AssetFixture.Source,
                Release = await database.Releases.SingleAsync() });
            await database.SaveChangesAsync();
            await new MetadataService(database, null!, null!, null!).RefreshAllAlbumCovers();
        }
        using var blockerScope = fixture.Services.CreateScope();
        var blocker = blockerScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Recordings.ExecuteUpdateAsync(update => update.SetProperty(recording => recording.Title, "blocked recording"));
        var provider = DispatchProxy.Create<IExternalMusicSearchService, MetadataProxy>();
        var worker = new CoverMetadataRefreshService(new MetadataScopeFactory(fixture.Services.GetRequiredService<IServiceScopeFactory>(), provider),
            fixture.Clock, NullLogger<CoverMetadataRefreshService>.Instance);
        var refreshing = worker.ProcessOneAsync(CancellationToken.None);
        using var observationScope = fixture.Services.CreateScope();
        var observation = observationScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.True(await WaitForBlockedQueryAsync(observation, "WITH inherited_recordings AS", refreshing));
        using var adminScope = fixture.Services.CreateScope();
        var admin = adminScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var customSource = "https://custom.invalid/admin-change.jpg";
        var updating = admin.Releases.Where(release => release.Id == releaseIdentifier)
            .ExecuteUpdateAsync(update => update.SetProperty(release => release.Cover, customSource));
        var adminWasBlocked = await WaitForBlockedQueryAsync(observation, "UPDATE \"Releases\"", updating);
        await blockerTransaction.CommitAsync();
        await refreshing.WaitAsync(TimeSpan.FromSeconds(10)); await updating.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(adminWasBlocked);
        Assert.Equal(customSource, await observation.Releases.Select(release => release.Cover).SingleAsync());
    }

    private static async Task<bool> WaitForBlockedQueryAsync(StreamingDbContext database, string queryFragment, Task operation)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var blocked = await database.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value" FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid()
                    AND wait_event_type = 'Lock' AND strpos(query, {queryFragment}) > 0
                """).SingleAsync();
            if (blocked > 0) return true;
            if (operation.IsCompleted) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        throw new TimeoutException("Expected the database operation to reach its lock wait.");
    }

    public class MetadataProxy : DispatchProxy
    {
        public int Requests { get; private set; }
        public Func<Task<ICollection<ReleaseDto>>> Response { get; set; } = () => Task.FromResult<ICollection<ReleaseDto>>(Array.Empty<ReleaseDto>());
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name != nameof(IExternalMusicSearchService.GetReleasesFromId)) throw new InvalidOperationException(method.Name);
            Requests++;
            return Response();
        }
    }

    private sealed class MetadataScopeFactory(IServiceScopeFactory inner, IExternalMusicSearchService metadata) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new MetadataScope(inner.CreateScope(), metadata);
    }
    private sealed class MetadataScope(IServiceScope inner, IExternalMusicSearchService metadata) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;
        public void Dispose() => inner.Dispose();
        public object? GetService(Type type) => type == typeof(IExternalMusicSearchService) ? metadata
            : type == typeof(IImageService) ? new ImageService(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string,string?> { ["Music:External:AlbumCover:BaseUrl"] = "https://coverartarchive.org/" }).Build())
            : inner.ServiceProvider.GetService(type);
    }
}
