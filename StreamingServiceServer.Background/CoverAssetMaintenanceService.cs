using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Background;

public sealed class CoverAssetMaintenanceService(IServiceScopeFactory scopes, ICoverAssetStore assets,
    IOptions<CoverAssetOptions> configuration, TimeProvider clock, ILogger<CoverAssetMaintenanceService> logger) : BackgroundService
{
    private readonly CoverAssetVerification verification = new(assets, configuration, clock);
    private bool legacyReferencesComplete;
    private int legacyReferenceOffset;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception failure) { logger.LogError(failure, "Cover maintenance failed; pending downloads remain independently scheduled."); }
            await Task.Delay(configuration.Value.MaintenanceInterval, clock, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await IsolateAsync(() => BackfillAsync(cancellationToken), "backfill", cancellationToken);
        await IsolateAsync(() => VerifyCompletedAsync(cancellationToken), "verification", cancellationToken);
        await IsolateAsync(() => PopulateLegacyReferencesAsync(cancellationToken), "reference migration", cancellationToken);
        await IsolateAsync(() => PruneVerificationStateAsync(cancellationToken), "integrity state cleanup", cancellationToken);
        await IsolateAsync(() => assets.PruneUnreferencedAsync(IsReferencedAsync, cancellationToken), "cleanup", cancellationToken);
    }

    private async Task IsolateAsync(Func<Task> operation, string stage, CancellationToken cancellationToken)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await operation();
            logger.LogDebug("Cover maintenance {Stage} completed in {ElapsedMilliseconds} milliseconds.",
                stage, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) { logger.LogWarning(failure, "Cover maintenance {Stage} failed.", stage); }
    }

    private async Task<bool> IsReferencedAsync(string identity, CancellationToken cancellationToken)
    {
        if (!legacyReferencesComplete) return true;
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        return await database.CoverAssetJobs.AnyAsync(job => job.AssetIdentity == identity, cancellationToken)
            || await database.CoverAssetJobs.AnyAsync(job => job.OriginalIdentity == identity, cancellationToken);
    }

    private async Task PopulateLegacyReferencesAsync(CancellationToken cancellationToken)
    {
        if (legacyReferencesComplete) return;
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var jobs = await database.CoverAssetJobs.Where(job => job.AssetManifest != string.Empty && job.AssetIdentity == string.Empty)
            .OrderBy(job => job.EntityKind).ThenBy(job => job.EntityIdentifier).Skip(legacyReferenceOffset)
            .Take(configuration.Value.MaintenanceBatchSize).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            var manifest = CoverSourceSet.ParseManifest(job.AssetManifest);
            if (manifest is null) { legacyReferenceOffset++; continue; }
            job.AssetIdentity = manifest.Identity;
            job.OriginalIdentity = manifest.SourceIdentity;
        }
        await database.SaveChangesAsync(cancellationToken);
        legacyReferencesComplete = jobs.Count < configuration.Value.MaintenanceBatchSize;
    }

    private async Task PruneVerificationStateAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var cutoff = clock.GetUtcNow() - configuration.Value.UnreferencedAssetRetention;
        var expired = await database.CoverAssetIntegrityStates.Where(state => state.NextVerificationAt < cutoff
            && !database.CoverAssetJobs.Any(job => job.AssetIdentity == state.Identity))
            .OrderBy(state => state.NextVerificationAt).Take(configuration.Value.MaintenanceBatchSize)
            .Select(state => state.Identity).ToArrayAsync(cancellationToken);
        await database.CoverAssetIntegrityStates.Where(state => expired.Contains(state.Identity)).ExecuteDeleteAsync(cancellationToken);
    }

    private static readonly TimeSpan BackfillRestartInterval = TimeSpan.FromDays(1);
    private async Task BackfillAsync(CancellationToken cancellationToken)
    {
        foreach (var kind in new[] { CoverEntityKind.Release, CoverEntityKind.Recording })
        {
            using var scope = scopes.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var now = clock.GetUtcNow();
            var state = await database.CoverBackfillStates.SingleOrDefaultAsync(state => state.EntityKind == kind, cancellationToken);
            if (state is null)
            {
                state = new CoverBackfillState { EntityKind = kind, NextRestartAt = now + BackfillRestartInterval };
                database.CoverBackfillStates.Add(state);
            }
            var positionAddedAt = state.PositionAddedAt;
            var positionIdentifier = state.PositionIdentifier ?? Guid.Empty;
            var sources = kind == CoverEntityKind.Release
                ? await database.Releases.Where(release => positionAddedAt == null || release.DateAdded > positionAddedAt
                        || release.DateAdded == positionAddedAt && release.Id.CompareTo(positionIdentifier) > 0)
                    .OrderBy(release => release.DateAdded).ThenBy(release => release.Id).Take(configuration.Value.MaintenanceBatchSize)
                    .Select(release => new BackfillRow(release.Id, release.DateAdded, release.Cover, release.SmallCover, release.VerySmallCover)).ToListAsync(cancellationToken)
                : await database.Recordings.Where(recording => positionAddedAt == null || recording.DateAdded > positionAddedAt
                        || recording.DateAdded == positionAddedAt && recording.Id.CompareTo(positionIdentifier) > 0)
                    .OrderBy(recording => recording.DateAdded).ThenBy(recording => recording.Id).Take(configuration.Value.MaintenanceBatchSize)
                    .Select(recording => new BackfillRow(recording.Id, recording.DateAdded, recording.Cover, recording.SmallCover, recording.VerySmallCover)).ToListAsync(cancellationToken);
            var identifiers = sources.Select(source => source.Identifier).ToArray();
            var existing = (await database.CoverAssetJobs.Where(job => job.EntityKind == kind && identifiers.Contains(job.EntityIdentifier))
                .Select(job => job.EntityIdentifier).ToListAsync(cancellationToken)).ToHashSet();
            foreach (var source in sources)
            {
                var captured = CoverSourceSet.Capture(source.Full, source.Small, source.Thumbnail);
                if (existing.Contains(source.Identifier) || captured.Candidates.Count == 0) continue;
                var job = new CoverAssetJob { EntityKind = kind, EntityIdentifier = source.Identifier };
                CoverJobInterceptor.Schedule(job, captured, now, CoverJobPriority.Backfill);
                database.CoverAssetJobs.Add(job);
            }
            if (sources.Count != 0)
            {
                state.PositionAddedAt = sources[^1].AddedAt;
                state.PositionIdentifier = sources[^1].Identifier;
            }
            if (sources.Count < configuration.Value.MaintenanceBatchSize && state.NextRestartAt <= now)
            {
                state.PositionAddedAt = null;
                state.PositionIdentifier = null;
                state.NextRestartAt = now + BackfillRestartInterval;
            }
            state.Version = Guid.NewGuid();
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed record BackfillRow(Guid Identifier, DateTime AddedAt, string? Full, string? Small, string? Thumbnail);

    private async Task VerifyCompletedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var now = clock.GetUtcNow();
        var jobs = await database.CoverAssetJobs.Where(job => job.CompletedAt != null && job.NextAttemptAt <= now)
            .OrderBy(job => job.NextAttemptAt).ThenBy(job => job.EntityIdentifier).Take(configuration.Value.MaintenanceBatchSize).ToListAsync(cancellationToken);
        var releaseIdentifiers = jobs.Where(job => job.EntityKind == CoverEntityKind.Release).Select(job => job.EntityIdentifier).ToArray();
        var recordingIdentifiers = jobs.Where(job => job.EntityKind == CoverEntityKind.Recording).Select(job => job.EntityIdentifier).ToArray();
        var releases = await database.Releases.AsNoTracking().Where(release => releaseIdentifiers.Contains(release.Id))
            .Select(release => new SourceRow(release.Id, release.Cover, release.SmallCover, release.VerySmallCover, release.LocalCover))
            .ToDictionaryAsync(source => source.Identifier, cancellationToken);
        var recordings = await database.Recordings.AsNoTracking().Where(recording => recordingIdentifiers.Contains(recording.Id))
            .Select(recording => new SourceRow(recording.Id, recording.Cover, recording.SmallCover, recording.VerySmallCover, recording.LocalCover))
            .ToDictionaryAsync(source => source.Identifier, cancellationToken);
        var identities = jobs.Select(job => CoverSourceSet.ParseManifest(job.AssetManifest)?.Identity)
            .Where(identity => identity is not null).Distinct().ToArray();
        var persistedChecks = await database.CoverAssetIntegrityStates.AsNoTracking().Where(state => identities.Contains(state.Identity))
            .ToDictionaryAsync(state => state.Identity, state => state.NextVerificationAt, cancellationToken);
        var checkedAssets = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            try
            {
                var sources = job.EntityKind == CoverEntityKind.Release ? releases : recordings;
                if (!sources.TryGetValue(job.EntityIdentifier, out var source) || source.Source.Candidates.Count == 0)
                {
                    await CoverDerivativeState.ClearAsync(database, job.EntityKind, job.EntityIdentifier, job.Version, sources.TryGetValue(job.EntityIdentifier, out var missingSource) ? missingSource.Local : null, cancellationToken);
                    database.CoverAssetJobs.Remove(job);
                    continue;
                }
                var result = CoverSourceSet.ParseManifest(job.AssetManifest);
                var valid = false;
                if (result is not null)
                {
                    if (!checkedAssets.TryGetValue(result.Identity, out valid))
                    {
                        persistedChecks.TryGetValue(result.Identity, out var nextIntegrityCheck);
                        valid = await verification.CheckAsync(result, cancellationToken, nextIntegrityCheck);
                        if (valid && nextIntegrityCheck <= now)
                        {
                            var nextCheck = now + configuration.Value.IntegrityInterval;
                            await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"CoverAssetIntegrityStates\" (\"Identity\", \"NextVerificationAt\") VALUES ({result.Identity}, {nextCheck}) ON CONFLICT (\"Identity\") DO UPDATE SET \"NextVerificationAt\" = GREATEST(\"CoverAssetIntegrityStates\".\"NextVerificationAt\", EXCLUDED.\"NextVerificationAt\")", cancellationToken);
                            persistedChecks[result.Identity] = nextCheck;
                        }
                        else if (!valid)
                            await database.CoverAssetIntegrityStates.Where(state => state.Identity == result.Identity).ExecuteDeleteAsync(cancellationToken);
                        checkedAssets[result.Identity] = valid;
                    }
                    job.AssetIdentity = result.Identity;
                    job.OriginalIdentity = result.SourceIdentity;
                }
                if (!source.Source.Matches(job) || result is null || !valid || source.Local != assets.PublicAddress(result.Full))
                {
                    await CoverDerivativeState.ClearAsync(database, job.EntityKind, job.EntityIdentifier, job.Version, source.Local, cancellationToken);
                    CoverJobInterceptor.Schedule(job, source.Source, now, CoverJobPriority.Repair);
                }
                else if (job.CompletedAt < now - configuration.Value.SourceLifetime)
                    CoverJobInterceptor.Schedule(job, source.Source, now, CoverJobPriority.Refresh);
                else job.NextAttemptAt = now + configuration.Value.RepairInterval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception failure)
            {
                job.NextAttemptAt = now + configuration.Value.RepairInterval;
                logger.LogWarning(failure, "Cover verification failed for {EntityKind}/{EntityIdentifier}.", job.EntityKind, job.EntityIdentifier);
            }
        }
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { database.ChangeTracker.Clear(); }
    }

    private sealed record SourceRow(Guid Identifier, string? Full, string? Small, string? Thumbnail, string? Local)
    {
        public CoverSourceSet Source => CoverSourceSet.Capture(Full, Small, Thumbnail);
    }
}
