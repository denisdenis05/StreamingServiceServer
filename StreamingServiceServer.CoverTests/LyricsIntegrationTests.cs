using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Lyrics;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class LyricsIntegrationTests
{
    [PostgresFact]
    public async Task DiscoveryIsBoundedIdempotentAndIncludesExistingAndNewRecordings()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database, includeSecond: true);
        var store = Store(database, fixture.Clock);
        await store.DiscoverAsync(LyricsTestData.OneCall, CancellationToken.None);
        Assert.Equal(LyricsTestData.OneCall, await database.RecordingLyrics.CountAsync());
        await store.DiscoverAsync(LyricsTestData.OneCall, CancellationToken.None);
        await store.DiscoverAsync(LyricsTestData.OneCall, CancellationToken.None);
        Assert.Equal(LyricsTestData.TwoCalls, await database.RecordingLyrics.CountAsync());
    }

    [PostgresFact]
    public async Task SeparateWorkersCannotClaimOverlappingProviderRequests()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var firstScope = fixture.Services.CreateScope();
        using var secondScope = fixture.Services.CreateScope();
        var firstDatabase = firstScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var secondDatabase = secondScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(firstDatabase, includeSecond: true);
        var firstStore = Store(firstDatabase, fixture.Clock);
        var secondStore = Store(secondDatabase, fixture.Clock);
        await firstStore.DiscoverAsync(LyricsTestData.TwoCalls, CancellationToken.None);
        var claims = await Task.WhenAll(firstStore.ClaimAsync(new LyricsOptions().LeaseLifetime, CancellationToken.None),
            secondStore.ClaimAsync(new LyricsOptions().LeaseLifetime, CancellationToken.None));
        Assert.Single(claims, lease => lease is not null);
    }

    [PostgresFact]
    public async Task SharedCooldownSurvivesStoreRecreationAndDelaysOtherSongs()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database, includeSecond: true);
        var store = Store(database, fixture.Clock);
        await store.DiscoverAsync(LyricsTestData.TwoCalls, CancellationToken.None);
        var lease = await store.ClaimAsync(new LyricsOptions().LeaseLifetime, CancellationToken.None);
        var retryAt = fixture.Clock.GetUtcNow() + LyricsTestData.ProviderCooldown;
        await store.FailAsync(lease!, LyricsProtocol.ProviderFailure, retryAt, CancellationToken.None);
        await store.ReleaseAsync(lease!, retryAt, CancellationToken.None);
        var failed = await database.RecordingLyrics.AsNoTracking().SingleAsync(job => job.RecordingIdentifier == lease!.Job.RecordingIdentifier);
        Assert.Equal(LyricsTestData.OneCall, failed.Attempts);
        Assert.Equal(LyricsDownloadState.Failed, failed.State);
        Assert.Equal(retryAt, failed.NextAttemptAt);
        using var newScope = fixture.Services.CreateScope();
        var recreated = Store(newScope.ServiceProvider.GetRequiredService<StreamingDbContext>(), fixture.Clock);
        Assert.Null(await recreated.ClaimAsync(new LyricsOptions().LeaseLifetime, CancellationToken.None));
        fixture.Clock.Advance(LyricsTestData.ProviderCooldown);
        Assert.NotNull(await recreated.ClaimAsync(new LyricsOptions().LeaseLifetime, CancellationToken.None));
    }

    [PostgresFact]
    public async Task ExpiredLeaseRecoversAndRejectsLateCompletionsAndLateGateRelease()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        var identifiers = new LyricsTestIdentifiers();
        var store = new LyricsJobStore(database, fixture.Clock, identifiers);
        await store.DiscoverAsync(LyricsTestData.OneCall, CancellationToken.None);
        var options = new LyricsOptions();
        var first = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        fixture.Clock.Advance(options.LeaseLifetime);
        var second = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        Assert.NotEqual(first!.Token, second!.Token);
        await store.CompleteAsync(first, LyricsTestData.Track, LyricsDownloadState.Downloaded, LyricsTestData.Content,
            string.Empty, fixture.Clock.GetUtcNow() + options.MetadataRecheckInterval, CancellationToken.None);
        await store.ReleaseAsync(first, fixture.Clock.GetUtcNow(), CancellationToken.None);
        var current = await database.RecordingLyrics.AsNoTracking().SingleAsync();
        Assert.Equal(second.Token, current.LeaseToken);
        Assert.Null(current.PlainLyrics);
        Assert.Equal(second.Token, (await database.LyricsQueueStates.AsNoTracking().SingleAsync()).LeaseToken);
        await store.CompleteAsync(second, LyricsTestData.Track, LyricsDownloadState.Downloaded, LyricsTestData.Content,
            string.Empty, fixture.Clock.GetUtcNow() + options.MetadataRecheckInterval, CancellationToken.None);
        Assert.Equal(LyricsTestData.Plain, (await database.RecordingLyrics.AsNoTracking().SingleAsync()).PlainLyrics);
    }

    [PostgresFact]
    public async Task LocalJsonAndFileExportsWorkWithoutAnyRegisteredExternalProvider()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        await DownloadAsync(database, fixture.Clock);
        using var restartedScope = fixture.Services.CreateScope();
        var restartedDatabase = restartedScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var reader = new LyricsReader(restartedDatabase, new LyricsCatalog(restartedDatabase));
        var response = await reader.ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None);
        Assert.Equal(LyricsDownloadState.Downloaded.ToString(), response!.State);
        Assert.Equal(LyricsTestData.Synced, response.SyncedLyrics);
        var controller = new LyricsController(reader);
        var synced = Assert.IsType<FileContentResult>(await controller.Download(LyricsTestData.RecordingIdentifier,
            LyricsExportFormat.Synced, CancellationToken.None));
        Assert.Equal(LyricsTestData.Synced, Encoding.UTF8.GetString(synced.FileContents));
        Assert.EndsWith(LyricsRoutes.SyncedExtension, synced.FileDownloadName);
        var plain = Assert.IsType<FileContentResult>(await controller.Download(LyricsTestData.RecordingIdentifier,
            LyricsExportFormat.Plain, CancellationToken.None));
        Assert.Equal(LyricsTestData.Plain, Encoding.UTF8.GetString(plain.FileContents));
        var rich = Assert.IsType<FileContentResult>(await controller.Download(LyricsTestData.RecordingIdentifier,
            LyricsExportFormat.Lyricsfile, CancellationToken.None));
        Assert.Equal(LyricsTestData.Lyricsfile, Encoding.UTF8.GetString(rich.FileContents));
    }

    [PostgresFact]
    public async Task PersistedDashboardFlagImmediatelyHidesEarlierLyricsAndRequeuesAfterCorrection()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        await DownloadAsync(database, fixture.Clock);
        var writer = new LyricsTrackMatchStore(database, fixture.Clock);
        await writer.SaveAsync(LyricsTestData.AlbumIdentifier, [LyricsTestData.RecordingIdentifier],
            LyricsTestData.Issues(LyricsTestData.NameMismatch), CancellationToken.None);
        var catalog = new LyricsCatalog(database);
        Assert.True((await catalog.ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None))!.HasNamingIssue);
        var reader = new LyricsReader(database, catalog);
        var hidden = await reader.ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None);
        Assert.Equal(LyricsDownloadState.Skipped.ToString(), hidden!.State);
        Assert.Null(hidden.PlainLyrics);
        Assert.Null(hidden.SyncedLyrics);
        Assert.IsType<NotFoundResult>(await new LyricsController(reader).Download(LyricsTestData.RecordingIdentifier));
        await writer.SaveAsync(LyricsTestData.AlbumIdentifier, [LyricsTestData.RecordingIdentifier], LyricsTestData.EmptyIssues, CancellationToken.None);
        Assert.False((await catalog.ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None))!.HasNamingIssue);
        Assert.Equal(fixture.Clock.GetUtcNow(), (await database.RecordingLyrics.AsNoTracking().SingleAsync()).NextAttemptAt);
    }

    [PostgresFact]
    public async Task UnreadableOrMissingAudioCannotEraseAnExistingDashboardMismatch()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        var writer = new LyricsTrackMatchStore(database, fixture.Clock);
        await writer.SaveAsync(LyricsTestData.AlbumIdentifier, [LyricsTestData.RecordingIdentifier],
            LyricsTestData.Issues(LyricsTestData.ArtistMismatch), CancellationToken.None);
        await writer.SaveAsync(LyricsTestData.AlbumIdentifier, [], LyricsTestData.EmptyIssues, CancellationToken.None);
        Assert.True((await new LyricsCatalog(database).ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None))!.HasNamingIssue);
    }

    [PostgresFact]
    public async Task PartialTagCheckRetainsPreviousFlagAndAddsNewMismatch()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        var writer = new LyricsTrackMatchStore(database, fixture.Clock);
        await writer.SaveAsync(LyricsTestData.AlbumIdentifier, [LyricsTestData.RecordingIdentifier],
            LyricsTestData.Issues(LyricsTestData.ArtistMismatch), CancellationToken.None);
        await writer.SaveAsync(LyricsTestData.AlbumIdentifier, [], LyricsTestData.Issues(LyricsTestData.NameMismatch), CancellationToken.None);
        var saved = (await database.AlbumAnalysisResults.AsNoTracking().SingleAsync()).TrackMatchIssuesJson;
        Assert.Contains(LyricsTestData.ArtistMismatch, saved);
        Assert.Contains(LyricsTestData.NameMismatch, saved);
        Assert.True((await new LyricsCatalog(database).ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None))!.HasNamingIssue);
    }

    [PostgresFact]
    public async Task TrackLengthFlagDoesNotExcludeLyricsAndMetadataChangesHideStaleText()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        await DownloadAsync(database, fixture.Clock);
        await new LyricsTrackMatchStore(database, fixture.Clock).SaveAsync(LyricsTestData.AlbumIdentifier,
            [LyricsTestData.RecordingIdentifier], LyricsTestData.Issues(LyricsTestData.LengthMismatch), CancellationToken.None);
        var reader = new LyricsReader(database, new LyricsCatalog(database));
        Assert.Equal(LyricsTestData.Synced, (await reader.ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None))!.SyncedLyrics);
        await database.Recordings.ExecuteUpdateAsync(update => update.SetProperty(recording => recording.Title, LyricsTestData.OtherTitle));
        var stale = await reader.ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None);
        Assert.Equal(LyricsDownloadState.Pending.ToString(), stale!.State);
        Assert.Null(stale.PlainLyrics);
    }

    [PostgresFact]
    public async Task RecordingCreditsOverrideTheAlbumArtistAndMissingDurationIsSkipped()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        database.RecordingArtistCredits.Add(new RecordingArtistCredit { Id = LyricsTestData.CreditIdentifier,
            RecordingId = LyricsTestData.RecordingIdentifier, Name = LyricsTestData.OtherArtist });
        await database.SaveChangesAsync();
        var catalog = new LyricsCatalog(database);
        Assert.Equal(LyricsTestData.OtherArtist, (await catalog.ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None))!.Artist);
        await database.Recordings.ExecuteUpdateAsync(update => update.SetProperty(recording => recording.Length, (int?)null));
        var response = await new LyricsReader(database, catalog).ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None);
        Assert.Equal(LyricsProtocol.InvalidMetadata, response!.Reason);
    }

    [PostgresFact]
    public async Task MissingRecordingsReturnNotFoundAndDeletionCascadesLyrics()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        await DownloadAsync(database, fixture.Clock);
        await database.Recordings.ExecuteDeleteAsync();
        Assert.Empty(await database.RecordingLyrics.ToArrayAsync());
        Assert.Null(await new LyricsReader(database, new LyricsCatalog(database)).ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None));
    }

    [PostgresFact]
    public async Task MigrationCreatesBothDurableTablesOnAFreshDatabase()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: true);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.Empty(await database.RecordingLyrics.ToArrayAsync());
        Assert.Empty(await database.LyricsQueueStates.ToArrayAsync());
    }

    [PostgresFact]
    public async Task QueueReportsPersistedStatesAndPagesWithoutReturningLyricsText()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        await DownloadAsync(database, fixture.Clock);
        var result = Assert.IsType<OkObjectResult>(await new LyricsQueueController(database, fixture.Clock).Get());
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains(LyricsDownloadState.Downloaded.ToString(), json);
        Assert.DoesNotContain(LyricsTestData.Plain, json);
        Assert.DoesNotContain(nameof(RecordingLyrics.PlainLyrics), json);
    }

    [PostgresFact]
    public async Task QueueFiltersByPersistedStateAndSearchesRecordingNames()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database, includeSecond: true);
        await Store(database, fixture.Clock).DiscoverAsync(LyricsTestData.TwoCalls, CancellationToken.None);
        var controller = new LyricsQueueController(database, fixture.Clock);
        var selected = Assert.IsType<OkObjectResult>(await controller.Get(state: LyricsDownloadState.Pending, search: LyricsTestData.OtherTitle));
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(selected.Value));
        var result = document.RootElement.GetProperty(LyricsTestData.JobsProperty).EnumerateArray().ToArray();
        Assert.Single(result);
        Assert.Equal(LyricsTestData.OtherTitle, result[ LyricsTestData.FirstIndex ].GetProperty(nameof(Recording.Title)).GetString());
        var missing = Assert.IsType<OkObjectResult>(await controller.Get(state: LyricsDownloadState.Downloaded));
        using var empty = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(missing.Value));
        Assert.Empty(empty.RootElement.GetProperty(LyricsTestData.JobsProperty).EnumerateArray());
    }

    [PostgresFact]
    public async Task InvalidExportAndQueueStatesAreRejectedWithoutServingText()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var reader = new LyricsReader(database, new LyricsCatalog(database));
        Assert.IsType<BadRequestObjectResult>(await new LyricsController(reader).Download(LyricsTestData.RecordingIdentifier,
            (LyricsExportFormat)LyricsTestData.InvalidEnumValue));
        Assert.IsType<BadRequestObjectResult>(await new LyricsQueueController(database, fixture.Clock)
            .Get(state: (LyricsDownloadState)LyricsTestData.InvalidEnumValue));
    }

    [PostgresFact]
    public async Task MalformedPreviousAnalysisIsRetainedUntilEveryTrackIsRechecked()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database, includeSecond: true);
        database.AlbumAnalysisResults.Add(new AlbumAnalysisResult { AlbumId = LyricsTestData.AlbumIdentifier,
            TrackMatchIssuesJson = LyricsTestData.InvalidJson });
        await database.SaveChangesAsync();
        await new LyricsTrackMatchStore(database, fixture.Clock).SaveAsync(LyricsTestData.AlbumIdentifier,
            [LyricsTestData.RecordingIdentifier], LyricsTestData.EmptyIssues, CancellationToken.None);
        Assert.Equal(LyricsTestData.InvalidJson, (await database.AlbumAnalysisResults.AsNoTracking().SingleAsync()).TrackMatchIssuesJson);
        await new LyricsTrackMatchStore(database, fixture.Clock).SaveAsync(LyricsTestData.AlbumIdentifier,
            [LyricsTestData.RecordingIdentifier, LyricsTestData.OtherRecordingIdentifier], LyricsTestData.EmptyIssues, CancellationToken.None);
        Assert.False((await new LyricsCatalog(database).ReadAsync(LyricsTestData.RecordingIdentifier, CancellationToken.None))!.HasNamingIssue);
    }

    [Fact]
    public void LyricsRoutesRequireAuthenticationAndQueueRequiresAdministratorRole()
    {
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(LyricsController), typeof(AuthorizeAttribute)));
        var queueAuthorization = Assert.IsType<AuthorizeAttribute>(Attribute.GetCustomAttribute(typeof(LyricsQueueController), typeof(AuthorizeAttribute)));
        Assert.Equal(LyricsRoutes.Administrator, queueAuthorization.Roles);
    }

    private static LyricsJobStore Store(StreamingDbContext database, TimeProvider clock) => new(database, clock, new LyricsTestIdentifiers());

    private static async Task DownloadAsync(StreamingDbContext database, TimeProvider clock)
    {
        var store = Store(database, clock);
        await store.DiscoverAsync(LyricsTestData.OneCall, CancellationToken.None);
        var options = new LyricsOptions();
        var lease = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        await store.CompleteAsync(lease!, LyricsTestData.Track, LyricsDownloadState.Downloaded, LyricsTestData.Content,
            string.Empty, clock.GetUtcNow() + options.MetadataRecheckInterval, CancellationToken.None);
        await store.ReleaseAsync(lease!, clock.GetUtcNow() + options.RequestInterval, CancellationToken.None);
    }

    private static async Task SeedAsync(StreamingDbContext database, bool includeSecond = false)
    {
        var release = new Release { Id = LyricsTestData.AlbumIdentifier, Title = LyricsTestData.Album, Cover = string.Empty,
            Artist = new Artist { Id = LyricsTestData.ArtistIdentifier, Name = LyricsTestData.Artist, SortName = LyricsTestData.Artist } };
        database.Recordings.Add(new Recording { Id = LyricsTestData.RecordingIdentifier, Title = LyricsTestData.Title,
            Length = LyricsTestData.DurationMilliseconds, Cover = string.Empty, Release = release });
        if (includeSecond) database.Recordings.Add(new Recording { Id = LyricsTestData.OtherRecordingIdentifier, Title = LyricsTestData.OtherTitle,
            Length = LyricsTestData.DurationMilliseconds, Cover = string.Empty, Release = release });
        await database.SaveChangesAsync();
    }
}
