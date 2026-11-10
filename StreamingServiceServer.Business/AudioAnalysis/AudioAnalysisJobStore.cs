using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.AudioAnalysis;

public sealed record AudioAnalysisLease(Guid RecordingId, Guid Version, Guid Token, int Attempts);

public interface IAudioAnalysisJobStore
{
    Task DiscoverAsync(int batchSize, CancellationToken cancellationToken);
    Task<AudioAnalysisLease?> ClaimAsync(AudioAnalysisConfiguration configuration, TimeSpan leaseLifetime, CancellationToken cancellationToken);
    Task<RecordingAudioAnalysis?> GetCurrentAsync(Guid recordingIdentifier, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(AudioAnalysisLease lease, AudioAnalysisConfiguration configuration, AudioAnalysisSource source,
        AudioAnalysisResult result, CancellationToken cancellationToken);
    Task FailAsync(AudioAnalysisLease lease, string safeCode, DateTimeOffset nextAttemptAt, bool unavailable, CancellationToken cancellationToken);
    Task ReleaseAsync(AudioAnalysisLease lease, CancellationToken cancellationToken);
}

public sealed class AudioAnalysisJobStore(StreamingDbContext database, TimeProvider clock) : IAudioAnalysisJobStore
{
    private const int NoRowsChanged = 0;
    private const int InitialAttempts = 0;
    private const int AttemptIncrement = 1;
    private const string DiscoverJobSql = """
        INSERT INTO "AudioAnalysisJobs"
            ("RecordingId", "State", "SourceVersion", "Version", "NextAttemptAt", "EnqueuedAt", "UpdatedAt", "Attempts")
        VALUES ({0}, {1}, {2}, {3}, {4}, {4}, {4}, {5})
        ON CONFLICT ("RecordingId") DO NOTHING
        """;
    public static readonly TimeSpan SourceRecheckInterval = TimeSpan.FromHours(24);

    public async Task DiscoverAsync(int batchSize, CancellationToken cancellationToken)
    {
        var recordingIdentifiers = await database.Recordings.AsNoTracking()
            .Where(recording => !database.AudioAnalysisJobs.Any(job => job.RecordingId == recording.Id))
            .OrderBy(recording => recording.Id).Select(recording => recording.Id).Take(batchSize).ToArrayAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var recordingIdentifier in recordingIdentifiers)
            await database.Database.ExecuteSqlRawAsync(DiscoverJobSql,
                new object[] { recordingIdentifier, (int)AudioAnalysisJobState.Pending, string.Empty, Guid.NewGuid(), now, InitialAttempts }, cancellationToken);
    }

    public async Task<AudioAnalysisLease?> ClaimAsync(AudioAnalysisConfiguration configuration, TimeSpan leaseLifetime, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var job = await database.AudioAnalysisJobs.AsNoTracking()
            .Where(current => current.NextAttemptAt <= now && (current.LeaseUntil == null || current.LeaseUntil <= now))
            .OrderBy(current => current.NextAttemptAt).ThenBy(current => current.RecordingId).FirstOrDefaultAsync(cancellationToken);
        if (job is null) return null;
        var lease = new AudioAnalysisLease(job.RecordingId, Guid.NewGuid(), Guid.NewGuid(), job.Attempts + AttemptIncrement);
        var leaseUntil = now + leaseLifetime;
        var changed = await database.AudioAnalysisJobs.Where(current => current.RecordingId == job.RecordingId
                && current.Version == job.Version && current.NextAttemptAt <= now
                && (current.LeaseUntil == null || current.LeaseUntil <= now)
                && database.AudioAnalysisSettings.Any(settings => settings.Id == AudioAnalysisStoragePolicy.SettingsIdentifier
                    && settings.Enabled && settings.Revision == configuration.Revision))
            .ExecuteUpdateAsync(update => update.SetProperty(current => current.Version, lease.Version)
                .SetProperty(current => current.LeaseToken, lease.Token).SetProperty(current => current.LeaseUntil, leaseUntil)
                .SetProperty(current => current.State, AudioAnalysisJobState.Processing).SetProperty(current => current.Attempts, lease.Attempts)
                .SetProperty(current => current.UpdatedAt, now), cancellationToken);
        return changed == NoRowsChanged ? null : lease;
    }

    public Task<RecordingAudioAnalysis?> GetCurrentAsync(Guid recordingIdentifier, CancellationToken cancellationToken) =>
        database.RecordingAudioAnalyses.AsNoTracking().SingleOrDefaultAsync(analysis => analysis.RecordingId == recordingIdentifier, cancellationToken);

    public async Task<bool> CompleteAsync(AudioAnalysisLease lease, AudioAnalysisConfiguration configuration, AudioAnalysisSource source,
        AudioAnalysisResult result, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var settingsLocked = await database.AudioAnalysisSettings.Where(settings => settings.Id == AudioAnalysisStoragePolicy.SettingsIdentifier
                && settings.Enabled && settings.Revision == configuration.Revision)
            .ExecuteUpdateAsync(update => update.SetProperty(settings => settings.Revision, configuration.Revision), cancellationToken);
        if (settingsLocked == NoRowsChanged) return false;
        var now = clock.GetUtcNow();
        var nextCheck = now + SourceRecheckInterval;
        var changed = await OwnedJob(lease).Where(job => job.LeaseUntil > now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, AudioAnalysisJobState.Completed)
                .SetProperty(job => job.SourceVersion, source.SourceVersion).SetProperty(job => job.CompletedAt, now)
                .SetProperty(job => job.NextAttemptAt, nextCheck).SetProperty(job => job.UpdatedAt, now)
                .SetProperty(job => job.Attempts, InitialAttempts).SetProperty(job => job.LastFailure, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null).SetProperty(job => job.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
        if (changed == NoRowsChanged) return false;
        var current = await database.RecordingAudioAnalyses.SingleOrDefaultAsync(analysis => analysis.RecordingId == lease.RecordingId, cancellationToken);
        if (current is null)
        {
            current = new RecordingAudioAnalysis { RecordingId = lease.RecordingId };
            database.RecordingAudioAnalyses.Add(current);
        }
        Apply(current, source, result, now);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task FailAsync(AudioAnalysisLease lease, string safeCode, DateTimeOffset nextAttemptAt, bool unavailable, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var state = unavailable ? AudioAnalysisJobState.Unavailable : AudioAnalysisJobState.Failed;
        return OwnedJob(lease).Where(job => job.LeaseUntil > now).ExecuteUpdateAsync(update => update.SetProperty(job => job.State, state)
            .SetProperty(job => job.LastFailure, safeCode).SetProperty(job => job.NextAttemptAt, nextAttemptAt)
            .SetProperty(job => job.UpdatedAt, now).SetProperty(job => job.LeaseToken, (Guid?)null)
            .SetProperty(job => job.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
    }

    public Task ReleaseAsync(AudioAnalysisLease lease, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return OwnedJob(lease).ExecuteUpdateAsync(update => update.SetProperty(job => job.State, AudioAnalysisJobState.Pending)
            .SetProperty(job => job.NextAttemptAt, now).SetProperty(job => job.UpdatedAt, now)
            .SetProperty(job => job.LeaseToken, (Guid?)null).SetProperty(job => job.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
    }

    private IQueryable<AudioAnalysisJob> OwnedJob(AudioAnalysisLease lease) => database.AudioAnalysisJobs
        .Where(job => job.RecordingId == lease.RecordingId && job.Version == lease.Version && job.LeaseToken == lease.Token);

    private static void Apply(RecordingAudioAnalysis current, AudioAnalysisSource source, AudioAnalysisResult result, DateTimeOffset now)
    {
        var previouslyAnalyzed = current.SourceVersion == source.SourceVersion && current.AudioSha256 == result.AudioSha256
            && current.Profile == result.Profile && current.EngineVersion == result.EngineVersion;
        current.SourceVersion = source.SourceVersion;
        current.AudioSha256 = result.AudioSha256;
        current.Profile = result.Profile;
        current.EngineName = result.EngineName;
        current.EngineVersion = result.EngineVersion;
        current.ProtocolVersion = result.ProtocolVersion;
        current.FeatureSchemaVersion = result.FeatureSchemaVersion;
        current.EmbeddingDimensions = result.EmbeddingDimensions;
        current.ModelsJson = result.ModelsJson;
        current.EmbeddingJson = result.EmbeddingJson;
        current.FeaturesJson = result.FeaturesJson;
        current.Tempo = result.Tempo;
        current.Energy = result.Energy;
        current.MusicalKey = result.MusicalKey;
        current.Scale = result.Scale;
        current.DurationSeconds = result.DurationSeconds;
        if (!previouslyAnalyzed) current.AnalyzedAt = now;
    }
}
