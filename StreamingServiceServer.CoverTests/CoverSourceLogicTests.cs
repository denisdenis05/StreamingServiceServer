using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class CoverSourceLogicTests
{
    private const string FullSource = "https://images.invalid/full.jpg";
    private const string SmallSource = "https://images.invalid/small.jpg";
    private const string ThumbnailSource = "https://images.invalid/thumbnail.jpg";
    [Fact]
    public void SourceCandidatesPreferFullAndRemoveDuplicates()
    {
        var sources = CoverSourceSet.Capture(FullSource, SmallSource, FullSource);
        Assert.Equal(new[] { FullSource, SmallSource }, sources.Candidates);
        Assert.Equal(FullSource, sources.PrimaryAddress);
    }
    [Fact]
    public void ThumbnailOnlyArtworkIsEligible()
    {
        var sources = CoverSourceSet.Capture(null, null, ThumbnailSource);
        Assert.Equal(ThumbnailSource, sources.PrimaryAddress);
        Assert.Single(sources.Candidates);
    }
    [Fact]
    public void SecondarySourceChangesInvalidateJobRevision()
    {
        var original = CoverSourceSet.Capture(FullSource, SmallSource, null);
        var job = new CoverAssetJob(); CoverJobInterceptor.Schedule(job, original, DateTimeOffset.UnixEpoch);
        Assert.True(original.Matches(job));
        Assert.False(CoverSourceSet.Capture(FullSource, ThumbnailSource, null).Matches(job));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Identity\":null}")]
    [InlineData("not-json")]
    public void IncompleteManifestIsRejectedWithoutDereferencingMissingVariants(string manifest) =>
        Assert.Null(CoverSourceSet.ParseManifest(manifest));

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ChangedSourceStopsSelectingOldDerivativesBeforeDownloadCompletes()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var release = await database.Releases.SingleAsync(); release.Cover = FullSource; await database.SaveChangesAsync();
        Assert.Empty(release.LocalCover); Assert.Empty(release.LocalSmallCover); Assert.Empty(release.LocalVerySmallCover);
        Assert.Empty(release.OptimizedCover);
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task RemovingAllSourcesClearsDerivativesAndDeletesPendingJob()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var release = await database.Releases.SingleAsync(); release.Cover = string.Empty; await database.SaveChangesAsync();
        Assert.Empty(release.LocalCover); Assert.Empty(await database.CoverAssetJobs.ToListAsync());
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ChangingOnlySmallSourceSchedulesANewRevision()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var release = await database.Releases.SingleAsync(); release.SmallCover = SmallSource; await database.SaveChangesAsync();
        var job = await database.CoverAssetJobs.SingleAsync(); Assert.Null(job.CompletedAt);
        Assert.Equal(CoverSourceSet.Capture(release.Cover, SmallSource, null).Revision, job.SourceRevision);
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task SmallerSourceIsUsedWhenFullSourceIsInvalid()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var release = await database.Releases.SingleAsync(); release.SmallCover = SmallSource; await database.SaveChangesAsync();
        }
        fixture.Assets.Handler.Wait = () =>
        {
            fixture.Assets.Handler.Content = fixture.Assets.Handler.Requests == 1 ? new byte[] { 0 } : AssetFixture.Image;
            return Task.CompletedTask;
        };
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope();
        var completed = await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.SingleAsync();
        Assert.NotNull(completed.CompletedAt); Assert.Equal(2, fixture.Assets.Handler.Requests);
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ReleasePublicationPreservesRecordingCustomSourceEvenWhenOldLocalMatches()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); var identifier = await fixture.AddReleaseAsync();
        var recordingIdentifier = await AddRecordingAsync(fixture, identifier, FullSource);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            await database.Releases.ExecuteUpdateAsync(update => update.SetProperty(release => release.LocalCover, "shared-old"));
            await database.Recordings.ExecuteUpdateAsync(update => update.SetProperty(recording => recording.LocalCover, "shared-old"));
            await database.CoverAssetJobs.Where(job => job.EntityKind == CoverEntityKind.Recording).ExecuteDeleteAsync();
        }
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope(); var current = await verification.ServiceProvider.GetRequiredService<StreamingDbContext>()
            .Recordings.SingleAsync(recording => recording.Id == recordingIdentifier);
        Assert.Equal(FullSource, current.Cover); Assert.Equal("shared-old", current.LocalCover);
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task PlaylistProjectionSelectsDownloadedTiersAndPreservesUnmatchedRows()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); var identifier = await fixture.AddReleaseAsync();
        var recordingIdentifier = await AddRecordingAsync(fixture, identifier, AssetFixture.Source);
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var user = new SocialMedia.Data.Models.User { Id = Guid.NewGuid(), Username = "playlist-cover", Email = "playlist@example.invalid",
            PasswordHash = "fixture-hash", ProviderId = "fixture-provider" };
        var playlist = new Playlist { Id = Guid.NewGuid(), Owner = user, Name = "cover playlist" };
        playlist.PlaylistRecordings.Add(new PlaylistRecording { Playlist = playlist, RecordingId = recordingIdentifier, AddedById = user.Id });
        playlist.PlaylistRecordings.Add(new PlaylistRecording { Playlist = playlist, RecordingId = Guid.NewGuid(), AddedById = user.Id,
            Cover = ThumbnailSource, Order = 1 });
        database.Playlists.Add(playlist); await database.SaveChangesAsync();
        var response = await new LibraryService(database, null!).GetPlaylistRecordingsAsync(playlist.Id, user.Id, 1, 10, CancellationToken.None);
        var recording = await database.Recordings.SingleAsync();
        Assert.Equal(recording.LocalCover, response[0].Cover); Assert.Equal(recording.LocalSmallCover, response[0].SmallCover);
        Assert.Equal(recording.LocalVerySmallCover, response[0].VerySmallCover); Assert.Equal(ThumbnailSource, response[1].Cover);
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ReleaseSourceReplacementUpdatesOnlyInheritedRecordingSources()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); var identifier = await fixture.AddReleaseAsync();
        var inheritedIdentifier = await AddRecordingAsync(fixture, identifier, AssetFixture.Source);
        var customIdentifier = await AddRecordingAsync(fixture, identifier, ThumbnailSource);
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var release = await database.Releases.SingleAsync(); release.Cover = FullSource; await database.SaveChangesAsync();
        }
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope(); var recordings = await verification.ServiceProvider
            .GetRequiredService<StreamingDbContext>().Recordings.ToDictionaryAsync(recording => recording.Id);
        Assert.Equal(FullSource, recordings[inheritedIdentifier].Cover);
        Assert.Equal(ThumbnailSource, recordings[customIdentifier].Cover);
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task MissingLocalAssetStopsBeingPreferredWhileRepairIsPending()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        foreach (var filename in Directory.GetFiles(fixture.Assets.Directory, "*-v1-small.jpg")) File.Delete(filename);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6)); await fixture.Worker.RepairAsync(CancellationToken.None);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var release = await database.Releases.SingleAsync(); Assert.Empty(release.LocalCover);
        Assert.Equal(AssetFixture.Source, StreamingServiceServer.Business.Services.Images.CoverPolicy.Select(
            release.LocalCover, release.OptimizedCover, release.Cover));
        Assert.Null((await database.CoverAssetJobs.SingleAsync()).CompletedAt);
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task SmallSourceChangedDuringDownloadCannotPublishOldDerivative()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Assets.Handler.Wait = async () => { started.TrySetResult(); await finish.Task; };
        var processing = fixture.Worker.ProcessDueAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var release = await database.Releases.SingleAsync(); release.SmallCover = SmallSource; await database.SaveChangesAsync();
        }
        finish.SetResult(); await processing;
        using var verification = fixture.Services.CreateScope(); var current = verification.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.Empty((await current.Releases.SingleAsync()).LocalCover);
        Assert.Null((await current.CoverAssetJobs.SingleAsync()).CompletedAt);
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task RecordingCustomSourceChangedDuringReleaseDownloadIsPreserved()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); var releaseIdentifier = await fixture.AddReleaseAsync();
        var recordingIdentifier = await AddRecordingAsync(fixture, releaseIdentifier, AssetFixture.Source);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs
                .Where(job => job.EntityKind == CoverEntityKind.Recording).ExecuteDeleteAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Assets.Handler.Wait = async () => { started.TrySetResult(); await finish.Task; };
        var processing = fixture.Worker.ProcessDueAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().Recordings.ExecuteUpdateAsync(update => update
                .SetProperty(recording => recording.Cover, FullSource).SetProperty(recording => recording.LocalCover, "shared-old"));
        finish.SetResult(); await processing;
        using var verification = fixture.Services.CreateScope(); var current = await verification.ServiceProvider
            .GetRequiredService<StreamingDbContext>().Recordings.SingleAsync(recording => recording.Id == recordingIdentifier);
        Assert.Equal(FullSource, current.Cover); Assert.Equal("shared-old", current.LocalCover);
    }
    private static async Task<Guid> AddRecordingAsync(DatabaseFixture fixture, Guid releaseIdentifier, string source)
    {
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var recording = new Recording { Id = Guid.NewGuid(), Title = "test recording", Cover = source,
            Release = await database.Releases.SingleAsync(release => release.Id == releaseIdentifier) };
        database.Recordings.Add(recording); await database.SaveChangesAsync(); return recording.Id;
    }
}
