using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Lyrics;

public sealed class LyricsJobStore(StreamingDbContext database, TimeProvider clock, ILyricsIdentifiers identifiers) : ILyricsJobStore
{
    private const string DiscoverSql = """
        INSERT INTO "RecordingLyrics" ("RecordingIdentifier", "State", "SourceRevision", "Attempts", "NextAttemptAt", "UpdatedAt", "LastFailure", "LookupVersion")
        SELECT "Id", {0}, {1}, {2}, {3}, {3}, {1}, {5} FROM "Recordings" recording
        WHERE NOT EXISTS (SELECT 1 FROM "RecordingLyrics" lyrics WHERE lyrics."RecordingIdentifier" = recording."Id")
        ORDER BY "Id" LIMIT {4} ON CONFLICT ("RecordingIdentifier") DO NOTHING
        """;
    private const string InitializeQueueSql = """
        INSERT INTO "LyricsQueueStates" ("Provider", "NextRequestAt") VALUES ({0}, {1}) ON CONFLICT ("Provider") DO NOTHING
        """;

    public Task DiscoverAsync(int batchSize, CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync(DiscoverSql,
        new object[] { (int)LyricsDownloadState.Pending, string.Empty, LyricsProtocol.InitialAttempts, clock.GetUtcNow(), batchSize, LyricsLookupVersions.Current }, cancellationToken);

    public async Task<LyricsLease?> ClaimAsync(TimeSpan leaseLifetime, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await database.Database.ExecuteSqlRawAsync(InitializeQueueSql, new object[] { LyricsProtocol.Provider, now }, cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var token = identifiers.Create();
        var leaseUntil = now + leaseLifetime;
        var gate = await database.LyricsQueueStates.Where(state => state.Provider == LyricsProtocol.Provider && state.NextRequestAt <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(state => state.LeaseToken, token)
                .SetProperty(state => state.NextRequestAt, leaseUntil), cancellationToken);
        if (gate == LyricsProtocol.NoRowsChanged) return null;
        var job = await DueJobs(now).AsNoTracking()
            .OrderBy(job => job.NextAttemptAt).ThenBy(job => job.RecordingIdentifier).FirstOrDefaultAsync(cancellationToken);
        if (job is null) return null;
        var changed = await DueJobs(now).Where(current => current.RecordingIdentifier == job.RecordingIdentifier)
            .ExecuteUpdateAsync(update => update.SetProperty(current => current.LeaseToken, token)
                .SetProperty(current => current.LeaseUntil, leaseUntil)
                .SetProperty(current => current.LookupVersion, LyricsLookupVersions.Current)
                .SetProperty(current => current.NextAttemptAt, now), cancellationToken);
        if (changed == LyricsProtocol.NoRowsChanged) return null;
        await transaction.CommitAsync(cancellationToken);
        job.LookupVersion = LyricsLookupVersions.Current;
        job.NextAttemptAt = now;
        return new LyricsLease(job, token);
    }

    public Task CompleteAsync(LyricsLease lease, LyricsTrack track, LyricsDownloadState state, LyricsContent? content,
        string failureCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return OwnedJob(lease).Where(job => job.LeaseUntil > now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, state)
                .SetProperty(job => job.SourceRevision, track.Revision).SetProperty(job => job.ProviderIdentifier, content == null ? null : content.ProviderIdentifier)
                .SetProperty(job => job.PlainLyrics, content == null ? null : content.PlainLyrics)
                .SetProperty(job => job.SyncedLyrics, content == null ? null : content.SyncedLyrics)
                .SetProperty(job => job.Lyricsfile, content == null ? null : content.Lyricsfile)
                .SetProperty(job => job.Attempts, LyricsProtocol.InitialAttempts).SetProperty(job => job.LastFailure, failureCode)
                .SetProperty(job => job.NextAttemptAt, nextAttemptAt).SetProperty(job => job.UpdatedAt, now)
                .SetProperty(job => job.LeaseToken, (Guid?)null).SetProperty(job => job.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
    }

    public async Task FailAsync(LyricsLease lease, string failureCode, DateTimeOffset nextAttemptAt,
        DateTimeOffset nextRequestAt, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var gate = await database.LyricsQueueStates.Where(state => state.Provider == LyricsProtocol.Provider
                && state.LeaseToken == lease.Token && database.RecordingLyrics.Any(job => job.RecordingIdentifier == lease.Job.RecordingIdentifier
                    && job.LeaseToken == lease.Token && job.LeaseUntil > now))
            .ExecuteUpdateAsync(update => update.SetProperty(state => state.LeaseToken, (Guid?)null)
                .SetProperty(state => state.NextRequestAt, nextRequestAt), cancellationToken);
        if (gate == LyricsProtocol.NoRowsChanged) return;
        var changed = await OwnedJob(lease).Where(job => job.LeaseUntil > now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, LyricsDownloadState.Failed)
                .SetProperty(job => job.Attempts, lease.Job.Attempts + LyricsProtocol.AttemptIncrement)
                .SetProperty(job => job.LastFailure, failureCode).SetProperty(job => job.NextAttemptAt, nextAttemptAt)
                .SetProperty(job => job.UpdatedAt, now).SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
        if (changed == LyricsProtocol.NoRowsChanged) return;
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReleaseAsync(LyricsLease lease, DateTimeOffset nextRequestAt, CancellationToken cancellationToken)
    {
        await OwnedJob(lease).ExecuteUpdateAsync(update => update.SetProperty(job => job.LeaseToken, (Guid?)null)
            .SetProperty(job => job.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
        await database.LyricsQueueStates.Where(state => state.Provider == LyricsProtocol.Provider && state.LeaseToken == lease.Token)
            .ExecuteUpdateAsync(update => update.SetProperty(state => state.LeaseToken, (Guid?)null)
                .SetProperty(state => state.NextRequestAt, nextRequestAt), cancellationToken);
    }

    private IQueryable<RecordingLyrics> DueJobs(DateTimeOffset now) => database.RecordingLyrics
        .Where(job => (job.LeaseUntil == null || job.LeaseUntil <= now)
            && (job.NextAttemptAt <= now || (job.LookupVersion < LyricsLookupVersions.Current
                && (job.State == LyricsDownloadState.NotFound
                    || (job.State == LyricsDownloadState.Skipped && job.LastFailure == LyricsProtocol.UnmatchedRecording)))));

    private IQueryable<RecordingLyrics> OwnedJob(LyricsLease lease) => database.RecordingLyrics
        .Where(job => job.RecordingIdentifier == lease.Job.RecordingIdentifier && job.LeaseToken == lease.Token);
}
