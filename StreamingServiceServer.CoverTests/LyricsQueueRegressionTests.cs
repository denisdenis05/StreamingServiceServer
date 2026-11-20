using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.Business.Lyrics;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class LyricsQueueRegressionTests
{
    private static readonly Guid AdditionalRecordingIdentifier = Guid.Parse("3a265174-83ee-4eb6-bf03-2236ac305a88");
    private const string PreviousMigration = "20261120185624_AddRecordingLyricsQueue";
    private const string LookupMigration = "20261120194359_AddLyricsLookupVersion";
    private const string InsertPreviousLyricsSql = """
        INSERT INTO "RecordingLyrics" ("RecordingIdentifier", "State", "SourceRevision", "Attempts", "NextAttemptAt", "UpdatedAt", "LastFailure", "ProviderIdentifier", "PlainLyrics", "SyncedLyrics")
        VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9})
        """;

    [PostgresFact]
    public async Task OldMissingAndUnmatchedResultsRetryOnceWithoutWaitingForTheirPreviousSchedule()
    {
        foreach (var state in new[] { LyricsDownloadState.NotFound, LyricsDownloadState.Skipped })
        {
            await using var fixture = await DatabaseFixture.CreateAsync();
            using var scope = fixture.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            await SeedAsync(database);
            var options = new LyricsOptions();
            var retryAt = fixture.Clock.GetUtcNow() + options.NotFoundRetryInterval;
            database.RecordingLyrics.Add(new RecordingLyrics { RecordingIdentifier = LyricsTestData.RecordingIdentifier,
                State = state, LastFailure = state == LyricsDownloadState.NotFound ? LyricsProtocol.MissingLyrics : LyricsProtocol.UnmatchedRecording,
                LookupVersion = LyricsLookupVersions.Original, NextAttemptAt = retryAt, UpdatedAt = fixture.Clock.GetUtcNow() });
            await database.SaveChangesAsync();
            var store = Store(database, fixture.Clock);
            var lease = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
            Assert.NotNull(lease);
            var claimed = await database.RecordingLyrics.AsNoTracking().SingleAsync();
            Assert.Equal(LyricsLookupVersions.Current, claimed.LookupVersion);
            Assert.Equal(fixture.Clock.GetUtcNow(), claimed.NextAttemptAt);
            await store.CompleteAsync(lease, LyricsTestData.Track, state, null, lease.Job.LastFailure, retryAt, CancellationToken.None);
            await store.ReleaseAsync(lease, fixture.Clock.GetUtcNow() + options.RequestInterval, CancellationToken.None);
            fixture.Clock.Advance(options.RequestInterval);
            using var restartedScope = fixture.Services.CreateScope();
            var restartedStore = Store(restartedScope.ServiceProvider.GetRequiredService<StreamingDbContext>(), fixture.Clock);
            Assert.Null(await restartedStore.ClaimAsync(options.LeaseLifetime, CancellationToken.None));
        }
    }

    [PostgresFact]
    public async Task UpgradeDoesNotRescheduleGoodResultsNamingExclusionsInvalidMetadataOrTransientFailures()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        var options = new LyricsOptions();
        var retryAt = fixture.Clock.GetUtcNow() + options.NotFoundRetryInterval;
        var exclusions = new[]
        {
            (LyricsDownloadState.Downloaded, string.Empty),
            (LyricsDownloadState.Instrumental, string.Empty),
            (LyricsDownloadState.Skipped, LyricsProtocol.NamingIssue),
            (LyricsDownloadState.Skipped, LyricsProtocol.InvalidMetadata),
            (LyricsDownloadState.Failed, LyricsProtocol.ProviderFailure)
        };
        database.RecordingLyrics.Add(new RecordingLyrics { RecordingIdentifier = LyricsTestData.RecordingIdentifier,
            LookupVersion = LyricsLookupVersions.Original, NextAttemptAt = retryAt, UpdatedAt = fixture.Clock.GetUtcNow(),
            PlainLyrics = LyricsTestData.Plain, SyncedLyrics = LyricsTestData.Synced });
        await database.SaveChangesAsync();
        var store = Store(database, fixture.Clock);
        foreach (var (state, failure) in exclusions)
        {
            await database.RecordingLyrics.ExecuteUpdateAsync(update => update.SetProperty(job => job.State, state)
                .SetProperty(job => job.LastFailure, failure));
            Assert.Null(await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None));
            var unchanged = await database.RecordingLyrics.AsNoTracking().SingleAsync();
            Assert.Equal(retryAt, unchanged.NextAttemptAt);
            Assert.Equal(LyricsLookupVersions.Original, unchanged.LookupVersion);
            Assert.Equal(LyricsTestData.Plain, unchanged.PlainLyrics);
        }
    }

    [PostgresFact]
    public async Task CrashedUpgradeClaimRecoversAtLeaseExpiryAndRejectsTheOldOwnersFailure()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        var options = new LyricsOptions();
        database.RecordingLyrics.Add(new RecordingLyrics { RecordingIdentifier = LyricsTestData.RecordingIdentifier,
            State = LyricsDownloadState.NotFound, LookupVersion = LyricsLookupVersions.Original,
            NextAttemptAt = fixture.Clock.GetUtcNow() + options.NotFoundRetryInterval, UpdatedAt = fixture.Clock.GetUtcNow() });
        await database.SaveChangesAsync();
        var store = Store(database, fixture.Clock);
        var oldLease = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        fixture.Clock.Advance(options.LeaseLifetime);
        var newLease = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        Assert.NotNull(oldLease);
        Assert.NotNull(newLease);
        await store.FailAsync(oldLease, LyricsProtocol.ProviderFailure, fixture.Clock.GetUtcNow() + options.MaximumRetryDelay,
            fixture.Clock.GetUtcNow() + options.MaximumRetryDelay, CancellationToken.None);
        await store.ReleaseAsync(oldLease, fixture.Clock.GetUtcNow(), CancellationToken.None);
        var current = await database.RecordingLyrics.AsNoTracking().SingleAsync();
        var gate = await database.LyricsQueueStates.AsNoTracking().SingleAsync();
        Assert.Equal(newLease.Token, current.LeaseToken);
        Assert.Equal(newLease.Token, gate.LeaseToken);
        Assert.Equal(fixture.Clock.GetUtcNow() + options.LeaseLifetime, gate.NextRequestAt);
        Assert.NotEqual(LyricsDownloadState.Failed, current.State);
    }

    [PostgresFact]
    public async Task FailurePersistsSharedCooldownBeforeReleaseAndOldReleaseCannotShortenIt()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database, includeSecond: true);
        var options = new LyricsOptions();
        var store = Store(database, fixture.Clock);
        await store.DiscoverAsync(LyricsTestData.TwoCalls, CancellationToken.None);
        var lease = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        Assert.NotNull(lease);
        var retryAt = fixture.Clock.GetUtcNow() + LyricsTestData.ProviderCooldown;
        await store.FailAsync(lease, LyricsProtocol.ProviderFailure, retryAt, retryAt, CancellationToken.None);
        using var restartedScope = fixture.Services.CreateScope();
        var restartedDatabase = restartedScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var restartedStore = Store(restartedDatabase, fixture.Clock);
        var failed = await restartedDatabase.RecordingLyrics.AsNoTracking().SingleAsync(job => job.RecordingIdentifier == lease.Job.RecordingIdentifier);
        Assert.Equal(LyricsDownloadState.Failed, failed.State);
        Assert.Null(failed.LeaseToken);
        Assert.Equal(retryAt, failed.NextAttemptAt);
        Assert.Equal(retryAt, (await restartedDatabase.LyricsQueueStates.AsNoTracking().SingleAsync()).NextRequestAt);
        await store.ReleaseAsync(lease, fixture.Clock.GetUtcNow(), CancellationToken.None);
        fixture.Clock.Advance(options.LeaseLifetime);
        Assert.Null(await restartedStore.ClaimAsync(options.LeaseLifetime, CancellationToken.None));
        fixture.Clock.Advance(LyricsTestData.ProviderCooldown);
        Assert.NotNull(await restartedStore.ClaimAsync(options.LeaseLifetime, CancellationToken.None));
    }

    [PostgresFact]
    public async Task FailedRecordingUpdateRollsBackTheProviderCooldownInTheSameTransaction()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database);
        var options = new LyricsOptions();
        var store = Store(database, fixture.Clock);
        await store.DiscoverAsync(LyricsTestData.OneCall, CancellationToken.None);
        var lease = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        Assert.NotNull(lease);
        var interceptedOptions = new DbContextOptionsBuilder<StreamingDbContext>()
            .UseNpgsql(database.Database.GetConnectionString()).AddInterceptors(new RejectRecordingFailureUpdate()).Options;
        await using var interceptedDatabase = new StreamingDbContext(interceptedOptions);
        var interruptedStore = Store(interceptedDatabase, fixture.Clock);
        var retryAt = fixture.Clock.GetUtcNow() + LyricsTestData.ProviderCooldown;
        await Assert.ThrowsAsync<InvalidOperationException>(() => interruptedStore.FailAsync(lease,
            LyricsProtocol.ProviderFailure, retryAt, retryAt, CancellationToken.None));
        var unchangedJob = await database.RecordingLyrics.AsNoTracking().SingleAsync();
        var unchangedGate = await database.LyricsQueueStates.AsNoTracking().SingleAsync();
        Assert.Equal(LyricsDownloadState.Pending, unchangedJob.State);
        Assert.Equal(lease.Token, unchangedJob.LeaseToken);
        Assert.Equal(lease.Token, unchangedGate.LeaseToken);
        Assert.Equal(fixture.Clock.GetUtcNow() + options.LeaseLifetime, unchangedGate.NextRequestAt);
    }

    [PostgresFact]
    public async Task PerRecordingBackoffAllowsOtherDueSongsAfterTheProviderRequestInterval()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await SeedAsync(database, includeSecond: true);
        var options = new LyricsOptions();
        var store = Store(database, fixture.Clock);
        await store.DiscoverAsync(LyricsTestData.TwoCalls, CancellationToken.None);
        var first = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        Assert.NotNull(first);
        var jobRetryAt = fixture.Clock.GetUtcNow() + options.MaximumRetryDelay;
        await store.FailAsync(first, LyricsProtocol.InvalidResponse, jobRetryAt,
            fixture.Clock.GetUtcNow() + options.RequestInterval, CancellationToken.None);
        Assert.Null(await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None));
        fixture.Clock.Advance(options.RequestInterval);
        var second = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotEqual(first.Job.RecordingIdentifier, second.Job.RecordingIdentifier);
        var failed = await database.RecordingLyrics.AsNoTracking().SingleAsync(job => job.RecordingIdentifier == first.Job.RecordingIdentifier);
        Assert.Equal(jobRetryAt, failed.NextAttemptAt);
    }

    [PostgresFact]
    public async Task MigrationUpgradesPopulatedPreviousSchemaWithoutDiscardingLyricsAndDiscoveryStillWorks()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: true);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        await SeedAsync(database, includeSecond: true);
        var options = new LyricsOptions();
        var now = fixture.Clock.GetUtcNow();
        await database.Database.ExecuteSqlRawAsync(InsertPreviousLyricsSql, new object[] { LyricsTestData.RecordingIdentifier,
            (int)LyricsDownloadState.Downloaded, LyricsTestData.Track.Revision, LyricsProtocol.InitialAttempts,
            now + options.MetadataRecheckInterval, now, string.Empty, LyricsTestData.ProviderIdentifier, LyricsTestData.Plain, LyricsTestData.Synced });
        await database.Database.ExecuteSqlRawAsync(InsertPreviousLyricsSql, new object[] { LyricsTestData.OtherRecordingIdentifier,
            (int)LyricsDownloadState.NotFound, LyricsTestData.Track.Revision, LyricsProtocol.InitialAttempts,
            now + options.NotFoundRetryInterval, now, LyricsProtocol.MissingLyrics, LyricsTestData.ProviderIdentifier, string.Empty, string.Empty });
        database.Recordings.Add(new Recording { Id = AdditionalRecordingIdentifier, Title = LyricsTestData.OtherTitle,
            Length = LyricsTestData.DurationMilliseconds, Cover = string.Empty, Release = await database.Releases.SingleAsync() });
        await database.SaveChangesAsync();
        await migrator.MigrateAsync();
        Assert.Contains(LookupMigration, await database.Database.GetAppliedMigrationsAsync());
        var preserved = await database.RecordingLyrics.AsNoTracking().SingleAsync(job => job.RecordingIdentifier == LyricsTestData.RecordingIdentifier);
        Assert.Equal(LyricsTestData.Plain, preserved.PlainLyrics);
        Assert.Equal(LyricsTestData.Synced, preserved.SyncedLyrics);
        Assert.Equal(LyricsDownloadState.Downloaded, preserved.State);
        Assert.Equal(LyricsLookupVersions.Original, preserved.LookupVersion);
        var store = Store(database, fixture.Clock);
        var upgradedMissing = await store.ClaimAsync(options.LeaseLifetime, CancellationToken.None);
        Assert.NotNull(upgradedMissing);
        Assert.Equal(LyricsTestData.OtherRecordingIdentifier, upgradedMissing.Job.RecordingIdentifier);
        await store.DiscoverAsync(LyricsTestData.TwoCalls, CancellationToken.None);
        var discovered = await database.RecordingLyrics.AsNoTracking().SingleAsync(job => job.RecordingIdentifier == AdditionalRecordingIdentifier);
        Assert.Equal(LyricsLookupVersions.Current, discovered.LookupVersion);
        Assert.Equal(LyricsDownloadState.Pending, discovered.State);
    }

    private static LyricsJobStore Store(StreamingDbContext database, TimeProvider clock) => new(database, clock, new SequentialLyricsIdentifiers());

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

    private sealed class RejectRecordingFailureUpdate : DbCommandInterceptor
    {
        private const string RecordingUpdatePrefix = "UPDATE \"RecordingLyrics\"";
        private const string InterruptedFailurePersistence = "interrupted_failure_persistence";

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith(RecordingUpdatePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException(InterruptedFailurePersistence);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class SequentialLyricsIdentifiers : ILyricsIdentifiers
    {
        private const int InitialSequence = 1;
        private const int SequenceIncrement = 1;
        private const short EmptyIdentifierSegment = 0;
        private const int IdentifierSuffixByteCount = 8;
        private int sequence = InitialSequence;

        public Guid Create()
        {
            var identifier = new Guid(sequence, EmptyIdentifierSegment, EmptyIdentifierSegment, new byte[IdentifierSuffixByteCount]);
            sequence += SequenceIncrement;
            return identifier;
        }
    }
}
