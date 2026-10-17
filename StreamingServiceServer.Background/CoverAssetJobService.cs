using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
namespace StreamingServiceServer.Background;

public sealed class CoverAssetJobService(IServiceScopeFactory scopes, ICoverAssetStore assets,
    IOptions<CoverAssetOptions> configuration, TimeProvider clock, ILogger<CoverAssetJobService> logger) : BackgroundService
{
    private DateTimeOffset nextPrune = DateTimeOffset.MinValue;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RepairAsync(stoppingToken);
                await ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception failure) { logger.LogError(failure, "Cover asset jobs could not complete their current pass."); }
            await Task.Delay(configuration.Value.JobPollInterval, clock, stoppingToken);
        }
    }
    public async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var now = clock.GetUtcNow();
        var jobs = await database.CoverAssetJobs.AsNoTracking().Where(job => job.CompletedAt == null && job.NextAttemptAt <= now
            && (job.LeaseUntil == null || job.LeaseUntil <= now)).OrderBy(job => job.NextAttemptAt).ThenBy(job => job.EntityIdentifier)
            .Take(configuration.Value.BatchSize).ToListAsync(cancellationToken);
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = configuration.Value.ParallelJobs,
            CancellationToken = cancellationToken }, async (job, token) => await ProcessAsync(job, token));
        var pending = await database.CoverAssetJobs.Where(job => job.CompletedAt == null).CountAsync(cancellationToken);
        var oldest = await database.CoverAssetJobs.Where(job => job.CompletedAt == null).Select(job => (DateTimeOffset?)job.EnqueuedAt).MinAsync(cancellationToken);
        logger.LogInformation("Cover jobs: {PendingCount} pending; oldest enqueue time {OldestEnqueuedAt}.", pending, oldest);
    }
    private async Task ProcessAsync(CoverAssetJob job, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var lease = Guid.NewGuid(); var now = clock.GetUtcNow(); var leaseUntil = now + configuration.Value.LeaseLifetime;
        var claimed = await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind && current.EntityIdentifier == job.EntityIdentifier
            && current.Version == job.Version && current.CompletedAt == null && (current.LeaseUntil == null || current.LeaseUntil <= now))
            .ExecuteUpdateAsync(update => update.SetProperty(current => current.LeaseToken, lease).SetProperty(current => current.LeaseUntil, leaseUntil), cancellationToken);
        if (claimed == 0) return;
        try
        {
            var currentSource = job.EntityKind == CoverEntityKind.Release
                ? await database.Releases.Where(release => release.Id == job.EntityIdentifier)
                    .Select(release => new { Address = release.Cover, Local = release.LocalCover }).SingleOrDefaultAsync(cancellationToken)
                : await database.Recordings.Where(recording => recording.Id == job.EntityIdentifier)
                    .Select(recording => new { Address = recording.Cover, Local = recording.LocalCover }).SingleOrDefaultAsync(cancellationToken);
            if (currentSource is null || string.IsNullOrWhiteSpace(currentSource.Address))
            {
                await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind && current.EntityIdentifier == job.EntityIdentifier
                    && current.Version == job.Version && current.LeaseToken == lease).ExecuteDeleteAsync(cancellationToken);
                return;
            }
            if (currentSource.Address != job.SourceAddress)
            {
                var replacementVersion = Guid.NewGuid();
                await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind && current.EntityIdentifier == job.EntityIdentifier
                    && current.Version == job.Version && current.LeaseToken == lease)
                    .ExecuteUpdateAsync(update => update.SetProperty(current => current.SourceAddress, currentSource.Address)
                        .SetProperty(current => current.Version, replacementVersion).SetProperty(current => current.NextAttemptAt, now)
                        .SetProperty(current => current.Attempts, 0).SetProperty(current => current.LeaseToken, (Guid?)null)
                        .SetProperty(current => current.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
                return;
            }
            CoverAssetSet? previousAssets = null;
            try { if (!string.IsNullOrEmpty(job.AssetManifest)) previousAssets = JsonSerializer.Deserialize<CoverAssetSet>(job.AssetManifest); } catch (JsonException) {}
            var generatedSource = previousAssets is not null && Uri.TryCreate(job.SourceAddress, UriKind.Absolute, out var previousAddress)
                && previousAddress.Segments.Last() == previousAssets.Full.Filename;
            var sourceWasLocal = currentSource.Local == job.SourceAddress;
            var requestAddress = sourceWasLocal ? assets.NormalizeLocalAddress(job.SourceAddress) : job.SourceAddress;
            var result = generatedSource ? await assets.RestoreAsync(previousAssets!, cancellationToken)
                : await assets.GetOrCreateAsync(requestAddress, cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var normalizedSource = (generatedSource || sourceWasLocal) ? assets.PublicAddress(result.Full) : CoverPolicy.RepairArchiveAddress(job.SourceAddress);
            var full = assets.PublicAddress(result.Full); var small = assets.PublicAddress(result.Small); var thumbnail = assets.PublicAddress(result.Thumbnail);
            int applied;
            if (job.EntityKind == CoverEntityKind.Release)
            {
                var previousLocal = await database.Releases.Where(release => release.Id == job.EntityIdentifier).Select(release => release.LocalCover).FirstOrDefaultAsync(cancellationToken);
                applied = await database.Releases.Where(release => release.Id == job.EntityIdentifier && release.Cover == job.SourceAddress)
                    .ExecuteUpdateAsync(update => update.SetProperty(release => release.Cover, normalizedSource).SetProperty(release => release.LocalCover, full).SetProperty(release => release.LocalSmallCover, small)
                        .SetProperty(release => release.LocalVerySmallCover, thumbnail), cancellationToken);
                if (applied != 0)
                {
                    await database.Recordings.Where(recording => recording.Release.Id == job.EntityIdentifier
                        && (recording.Cover == job.SourceAddress || (previousLocal != null && previousLocal != string.Empty && recording.LocalCover == previousLocal)
                            || recording.Cover == null || recording.Cover == string.Empty))
                        .ExecuteUpdateAsync(update => update.SetProperty(recording => recording.Cover, normalizedSource)
                            .SetProperty(recording => recording.LocalCover, full).SetProperty(recording => recording.LocalSmallCover, small)
                            .SetProperty(recording => recording.LocalVerySmallCover, thumbnail), cancellationToken);
                }
            }
            else
            {
                applied = await database.Recordings.Where(recording => recording.Id == job.EntityIdentifier && recording.Cover == job.SourceAddress)
                    .ExecuteUpdateAsync(update => update.SetProperty(recording => recording.Cover, normalizedSource).SetProperty(recording => recording.LocalCover, full).SetProperty(recording => recording.LocalSmallCover, small)
                        .SetProperty(recording => recording.LocalVerySmallCover, thumbnail), cancellationToken);
            }
            var nextCheck = clock.GetUtcNow() + configuration.Value.RepairInterval;
            var completed = await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind && current.EntityIdentifier == job.EntityIdentifier
                && current.Version == job.Version && current.LeaseToken == lease)
                .ExecuteUpdateAsync(update => update.SetProperty(current => current.CompletedAt, clock.GetUtcNow())
                    .SetProperty(current => current.AssetManifest, applied == 0 ? string.Empty : JsonSerializer.Serialize(result, (JsonSerializerOptions?)null))
                    .SetProperty(current => current.NextAttemptAt, nextCheck).SetProperty(current => current.LastFailure, string.Empty)
                    .SetProperty(current => current.LeaseToken, (Guid?)null).SetProperty(current => current.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
            if (completed == 0) await transaction.RollbackAsync(cancellationToken); else await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            var attempts = job.Attempts + 1; var nextAttempt = clock.GetUtcNow() + RetryDelay(attempts, configuration.Value);
            await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind && current.EntityIdentifier == job.EntityIdentifier
                && current.Version == job.Version && current.LeaseToken == lease).ExecuteUpdateAsync(update => update.SetProperty(current => current.Attempts, attempts)
                .SetProperty(current => current.NextAttemptAt, nextAttempt).SetProperty(current => current.LastFailure, failure.GetType().Name)
                .SetProperty(current => current.LeaseToken, (Guid?)null).SetProperty(current => current.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
            logger.LogWarning(failure, "Cover job for {EntityKind}/{EntityIdentifier} failed; retry {NextAttemptAt}.", job.EntityKind, job.EntityIdentifier, nextAttempt);
        }
    }
    public static TimeSpan RetryDelay(int attempts, CoverAssetOptions options) => TimeSpan.FromTicks((long)Math.Min(options.MaximumRetryDelay.Ticks,
        options.InitialRetryDelay.Ticks * Math.Pow(2, Math.Min(attempts - 1, 20))));
    public async Task RepairAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        foreach (var kind in new[] { CoverEntityKind.Release, CoverEntityKind.Recording })
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sources = kind == CoverEntityKind.Release
                    ? await database.Releases.Where(release => release.Cover != null && release.Cover != string.Empty
                        && !database.CoverAssetJobs.Any(job => job.EntityKind == kind && job.EntityIdentifier == release.Id))
                        .OrderBy(release => release.DateAdded).ThenBy(release => release.Id).Take(configuration.Value.BatchSize)
                        .Select(release => new { Identifier = release.Id, Address = release.Cover }).ToListAsync(cancellationToken)
                    : await database.Recordings.Where(recording => recording.Cover != null && recording.Cover != string.Empty
                        && !database.CoverAssetJobs.Any(job => job.EntityKind == kind && job.EntityIdentifier == recording.Id))
                        .OrderBy(recording => recording.DateAdded).ThenBy(recording => recording.Id).Take(configuration.Value.BatchSize)
                        .Select(recording => new { Identifier = recording.Id, Address = recording.Cover }).ToListAsync(cancellationToken);
                if (sources.Count == 0) break;
                foreach (var source in sources)
                {
                    var job = new CoverAssetJob { EntityKind = kind, EntityIdentifier = source.Identifier };
                    CoverJobInterceptor.Schedule(job, source.Address, clock.GetUtcNow()); database.CoverAssetJobs.Add(job);
                }
                try { await database.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateException) { database.ChangeTracker.Clear(); break; }
                database.ChangeTracker.Clear();
                break;
            }
        }
        var now = clock.GetUtcNow();
        var completedJobs = await database.CoverAssetJobs.Where(job => job.CompletedAt != null && job.NextAttemptAt <= now)
            .OrderBy(job => job.NextAttemptAt).Take(configuration.Value.BatchSize).ToListAsync(cancellationToken);
        foreach (var job in completedJobs)
        {
            var currentSource = job.EntityKind == CoverEntityKind.Release
                ? await database.Releases.Where(release => release.Id == job.EntityIdentifier).Select(release => release.Cover).FirstOrDefaultAsync(cancellationToken)
                : await database.Recordings.Where(recording => recording.Id == job.EntityIdentifier).Select(recording => recording.Cover).FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(currentSource)) { database.CoverAssetJobs.Remove(job); continue; }
            CoverAssetSet? result = null;
            try { if (!string.IsNullOrEmpty(job.AssetManifest)) result = JsonSerializer.Deserialize<CoverAssetSet>(job.AssetManifest); } catch (JsonException) {}
            var localAddress = job.EntityKind == CoverEntityKind.Release
                ? await database.Releases.Where(release => release.Id == job.EntityIdentifier).Select(release => release.LocalCover).FirstOrDefaultAsync(cancellationToken)
                : await database.Recordings.Where(recording => recording.Id == job.EntityIdentifier).Select(recording => recording.LocalCover).FirstOrDefaultAsync(cancellationToken);
            if (job.SourceAddress != currentSource || job.CompletedAt < now - configuration.Value.SourceLifetime
                || result is null || localAddress != assets.PublicAddress(result.Full) || !await assets.IsValidAsync(result, cancellationToken)) CoverJobInterceptor.Schedule(job, currentSource, now);
            else job.NextAttemptAt = now + configuration.Value.RepairInterval;
        }
        try { await database.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { database.ChangeTracker.Clear(); }
        if (now >= nextPrune)
        {
            var manifests = await database.CoverAssetJobs.Where(job => job.AssetManifest != string.Empty).Select(job => job.AssetManifest).ToListAsync(cancellationToken);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var manifest in manifests)
            {
                try { var result = JsonSerializer.Deserialize<CoverAssetSet>(manifest); if (result is not null) { identities.Add(result.Identity); identities.Add(result.SourceIdentity); } } catch (JsonException) {}
            }
            await assets.PruneAsync(identities, cancellationToken);
            nextPrune = now + TimeSpan.FromDays(1);
        }
    }
}
