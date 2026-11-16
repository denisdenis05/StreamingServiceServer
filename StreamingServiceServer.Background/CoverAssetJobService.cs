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
    IOptions<CoverAssetOptions> configuration, TimeProvider clock, ILogger<CoverAssetJobService> logger,
    IAlbumCoverFallback? albumFallback = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception failure) { logger.LogError(failure, "Cover asset processing could not complete its current pass."); }
            await Task.Delay(configuration.Value.JobPollInterval, clock, stoppingToken);
        }
    }
    private long schedulingSequence = 1;
    public async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        var started = 0;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var active = new Dictionary<Task, Guid>();
        var attempted = new HashSet<(CoverEntityKind Kind, Guid Identifier)>();
        try
        {
            while (started < configuration.Value.BatchSize || active.Count > 0)
            {
                while (started < configuration.Value.BatchSize && active.Count < configuration.Value.ParallelJobs)
                {
                    using var scope = scopes.CreateScope();
                    var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
                    var now = clock.GetUtcNow();
                    var releaseIdentifiers = attempted.Where(entry => entry.Kind == CoverEntityKind.Release).Select(entry => entry.Identifier).ToArray();
                    var recordingIdentifiers = attempted.Where(entry => entry.Kind == CoverEntityKind.Recording).Select(entry => entry.Identifier).ToArray();
                    var available = database.CoverAssetJobs.AsNoTracking().Where(job => !job.Suspended && job.CompletedAt == null
                        && job.NextAttemptAt <= now && (job.LeaseUntil == null || job.LeaseUntil <= now)
                        && !(job.EntityKind == CoverEntityKind.Release && releaseIdentifiers.Contains(job.EntityIdentifier))
                        && !(job.EntityKind == CoverEntityKind.Recording && recordingIdentifiers.Contains(job.EntityIdentifier)));
                    var oldestTurn = schedulingSequence++ % CoverJobScheduling.FairnessPeriod == 0;
                    var ordered = oldestTurn ? available.OrderBy(job => job.EnqueuedAt).ThenBy(job => job.EntityIdentifier)
                        : available.OrderBy(job => job.Priority).ThenBy(job => job.EnqueuedAt).ThenBy(job => job.EntityIdentifier);
                    var job = await ordered.FirstOrDefaultAsync(cancellationToken);
                    if (job is null) break;
                    attempted.Add((job.EntityKind, job.EntityIdentifier));
                    active.Add(ProcessAsync(job, cancellationToken), job.EntityIdentifier);
                    started++;
                }
                if (active.Count == 0) break;
                var completed = await Task.WhenAny(active.Keys);
                active.Remove(completed);
                await completed;
            }
        }
        finally { await Task.WhenAll(active.Keys); }
        using var reportingScope = scopes.CreateScope();
        var reportingDatabase = reportingScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var pending = await reportingDatabase.CoverAssetJobs.Where(job => !job.Suspended && job.CompletedAt == null).CountAsync(cancellationToken);
        var oldest = await reportingDatabase.CoverAssetJobs.Where(job => !job.Suspended && job.CompletedAt == null)
            .Select(job => (DateTimeOffset?)job.EnqueuedAt).MinAsync(cancellationToken);
        logger.LogInformation("Cover processing admitted {AdmissionCount} jobs in {ElapsedMilliseconds} milliseconds; {PendingCount} pending; oldest {OldestEnqueuedAt}.",
            started, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, pending, oldest);
    }
    private async Task ProcessAsync(CoverAssetJob job, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var lease = Guid.NewGuid(); var now = clock.GetUtcNow();
        var leaseUntil = now + configuration.Value.LeaseLifetime;
        var claimed = await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind
            && current.EntityIdentifier == job.EntityIdentifier && current.Version == job.Version && current.CompletedAt == null
            && (current.LeaseUntil == null || current.LeaseUntil <= now))
            .ExecuteUpdateAsync(update => update.SetProperty(current => current.LeaseToken, lease)
                .SetProperty(current => current.LeaseUntil, leaseUntil), cancellationToken);
        if (claimed == 0) return;
        var stoppingToken = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(configuration.Value.OperationTimeout);
        cancellationToken = deadline.Token;
        try
        {
            var sources = await CoverSourceSet.ReadAsync(database, job.EntityKind, job.EntityIdentifier, cancellationToken);
            if (sources is null || sources.Candidates.Count == 0)
            {
                await CoverDerivativeState.ClearAsync(database, job.EntityKind, job.EntityIdentifier, job.Version, null, cancellationToken);
                await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind
                    && current.EntityIdentifier == job.EntityIdentifier && current.Version == job.Version && current.LeaseToken == lease)
                    .ExecuteDeleteAsync(cancellationToken);
                return;
            }
            if (!sources.Matches(job))
            {
                var replacementVersion = Guid.NewGuid();
                await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind && current.EntityIdentifier == job.EntityIdentifier
                    && current.Version == job.Version && current.LeaseToken == lease)
                    .ExecuteUpdateAsync(update => update.SetProperty(current => current.SourceAddress, sources.PrimaryAddress)
                        .SetProperty(current => current.SourceRevision, sources.Revision).SetProperty(current => current.Version, replacementVersion)
                        .SetProperty(current => current.NextAttemptAt, now).SetProperty(current => current.Attempts, 0)
                        .SetProperty(current => current.Suspended, false).SetProperty(current => current.LeaseToken, (Guid?)null)
                        .SetProperty(current => current.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
                return;
            }
            var previousAssets = CoverSourceSet.ParseManifest(job.AssetManifest);
            var local = job.EntityKind == CoverEntityKind.Release
                ? await database.Releases.Where(release => release.Id == job.EntityIdentifier).Select(release => release.LocalCover).SingleAsync(cancellationToken)
                : await database.Recordings.Where(recording => recording.Id == job.EntityIdentifier).Select(recording => recording.LocalCover).SingleAsync(cancellationToken);
            var generatedSource = previousAssets is not null && Uri.TryCreate(sources.PrimaryAddress, UriKind.Absolute, out var previousAddress)
                && previousAddress.Segments.Last() == previousAssets.Full.Filename;
            var sourceWasLocal = local == sources.PrimaryAddress;
            var result = generatedSource ? await assets.RestoreAsync(previousAssets!, cancellationToken)
                : await DownloadCandidatesAsync(database, job, sources, sourceWasLocal, cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var full = assets.PublicAddress(result.Full); var small = assets.PublicAddress(result.Small);
            var thumbnail = assets.PublicAddress(result.Thumbnail);
            var normalizedSource = generatedSource || sourceWasLocal ? full : CoverPolicy.RepairArchiveAddress(sources.Full);
            var publishedSources = CoverSourceSet.Capture(normalizedSource, sources.Small, sources.Thumbnail);
            var inheritedSources = CoverSourceSet.ParseRevision(job.InheritedSourceRevision);
            var manifest = JsonSerializer.Serialize(result);
            var completedAt = clock.GetUtcNow(); var nextCheck = completedAt + configuration.Value.RepairInterval;
            int applied;
            if (job.EntityKind == CoverEntityKind.Release)
            {
                applied = await database.Releases.Where(release => release.Id == job.EntityIdentifier
                    && (release.Cover ?? string.Empty) == sources.Full && (release.SmallCover ?? string.Empty) == sources.Small
                    && (release.VerySmallCover ?? string.Empty) == sources.Thumbnail)
                    .ExecuteUpdateAsync(update => update.SetProperty(release => release.Cover, normalizedSource)
                        .SetProperty(release => release.LocalCover, full).SetProperty(release => release.LocalSmallCover, small)
                        .SetProperty(release => release.LocalVerySmallCover, thumbnail), cancellationToken);
                if (applied != 0)
                {
                    var inherited = database.Recordings.Where(recording => recording.Release.Id == job.EntityIdentifier
                        && (((recording.Cover ?? string.Empty) == sources.Full && (recording.SmallCover ?? string.Empty) == sources.Small
                            && (recording.VerySmallCover ?? string.Empty) == sources.Thumbnail)
                            || (inheritedSources != null && (recording.Cover ?? string.Empty) == inheritedSources.Full
                                && (recording.SmallCover ?? string.Empty) == inheritedSources.Small
                                && (recording.VerySmallCover ?? string.Empty) == inheritedSources.Thumbnail)
                            || ((recording.Cover == null || recording.Cover == string.Empty)
                                && (recording.SmallCover == null || recording.SmallCover == string.Empty)
                                && (recording.VerySmallCover == null || recording.VerySmallCover == string.Empty))));
                    await database.CoverAssetJobs.Where(current => current.EntityKind == CoverEntityKind.Recording
                        && inherited.Any(recording => recording.Id == current.EntityIdentifier)
                        && (current.SourceRevision == sources.Revision || current.SourceRevision == job.InheritedSourceRevision || current.SourceRevision == string.Empty))
                        .ExecuteUpdateAsync(update => update.SetProperty(current => current.CompletedAt, (DateTimeOffset?)completedAt)
                            .SetProperty(current => current.AssetManifest, manifest).SetProperty(current => current.AssetIdentity, result.Identity)
                            .SetProperty(current => current.OriginalIdentity, result.SourceIdentity)
                            .SetProperty(current => current.SourceAddress, publishedSources.PrimaryAddress)
                            .SetProperty(current => current.SourceRevision, publishedSources.Revision)
                            .SetProperty(current => current.NextAttemptAt, nextCheck).SetProperty(current => current.LastFailure, string.Empty)
                            .SetProperty(current => current.LeaseToken, (Guid?)null).SetProperty(current => current.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
                    await inherited.ExecuteUpdateAsync(update => update.SetProperty(recording => recording.Cover, normalizedSource)
                        .SetProperty(recording => recording.SmallCover, sources.Small).SetProperty(recording => recording.VerySmallCover, sources.Thumbnail)
                        .SetProperty(recording => recording.LocalCover, full).SetProperty(recording => recording.LocalSmallCover, small)
                        .SetProperty(recording => recording.LocalVerySmallCover, thumbnail), cancellationToken);
                }
            }
            else
            {
                applied = await database.Recordings.Where(recording => recording.Id == job.EntityIdentifier
                    && (recording.Cover ?? string.Empty) == sources.Full && (recording.SmallCover ?? string.Empty) == sources.Small
                    && (recording.VerySmallCover ?? string.Empty) == sources.Thumbnail)
                    .ExecuteUpdateAsync(update => update.SetProperty(recording => recording.Cover, normalizedSource)
                        .SetProperty(recording => recording.LocalCover, full).SetProperty(recording => recording.LocalSmallCover, small)
                        .SetProperty(recording => recording.LocalVerySmallCover, thumbnail), cancellationToken);
            }
            var completed = await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind
                && current.EntityIdentifier == job.EntityIdentifier && current.Version == job.Version && current.LeaseToken == lease)
                .ExecuteUpdateAsync(update => update.SetProperty(current => current.CompletedAt, (DateTimeOffset?)completedAt)
                    .SetProperty(current => current.AssetManifest, applied == 0 ? string.Empty : manifest)
                    .SetProperty(current => current.AssetIdentity, applied == 0 ? string.Empty : result.Identity)
                    .SetProperty(current => current.OriginalIdentity, applied == 0 ? string.Empty : result.SourceIdentity)
                    .SetProperty(current => current.SourceAddress, publishedSources.PrimaryAddress)
                    .SetProperty(current => current.SourceRevision, publishedSources.Revision)
                    .SetProperty(current => current.InheritedSourceRevision, string.Empty)
                    .SetProperty(current => current.NextAttemptAt, nextCheck).SetProperty(current => current.LastFailure, string.Empty)
                    .SetProperty(current => current.LeaseToken, (Guid?)null).SetProperty(current => current.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
            if (completed == 0) await transaction.RollbackAsync(cancellationToken);
            else await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            var attempts = job.Attempts + 1;
            var failures = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : new[] { failure };
            var permanent = failures.Length > 0 && failures.All(candidate => candidate is CoverSourceFailure { IsPermanent: true });
            var retryAfter = failures.OfType<CoverSourceFailure>().Select(candidate => candidate.RetryAfter ?? TimeSpan.Zero)
                .DefaultIfEmpty(TimeSpan.Zero).Max();
            var delay = RetryDelay(attempts, configuration.Value);
            var nextAttempt = clock.GetUtcNow() + (retryAfter > delay ? retryAfter : delay);
            await database.CoverAssetJobs.Where(current => current.EntityKind == job.EntityKind && current.EntityIdentifier == job.EntityIdentifier
                && current.Version == job.Version && current.LeaseToken == lease).ExecuteUpdateAsync(update => update.SetProperty(current => current.Attempts, attempts)
                .SetProperty(current => current.NextAttemptAt, nextAttempt).SetProperty(current => current.Suspended, permanent)
                .SetProperty(current => current.LastFailure, failure.GetType().Name)
                .SetProperty(current => current.LeaseToken, (Guid?)null).SetProperty(current => current.LeaseUntil, (DateTimeOffset?)null), stoppingToken);
            logger.LogWarning(failure, "Cover job for {EntityKind}/{EntityIdentifier} failed; retry {NextAttemptAt}.", job.EntityKind, job.EntityIdentifier, nextAttempt);
        }
    }
    private async Task<CoverAssetSet> DownloadCandidatesAsync(StreamingDbContext database, CoverAssetJob job,
        CoverSourceSet sources, bool sourceWasLocal, CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        var fallbackAttempted = false;
        foreach (var candidate in sources.Candidates)
        {
            try { return await assets.GetOrCreateAsync(sourceWasLocal ? assets.NormalizeLocalAddress(candidate) : candidate, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception failure) when (failure is IOException or HttpRequestException or OperationCanceledException)
            {
                failures.Add(failure);
                if (sourceWasLocal || fallbackAttempted || albumFallback is null || !IsArchiveSource(candidate)) continue;
                fallbackAttempted = true;
                try
                {
                    var album = job.EntityKind == CoverEntityKind.Release
                        ? await database.Releases.Where(release => release.Id == job.EntityIdentifier)
                            .Select(release => new { Artist = release.Artist.Name, Album = release.Title }).SingleOrDefaultAsync(cancellationToken)
                        : await database.Recordings.Where(recording => recording.Id == job.EntityIdentifier)
                            .Select(recording => new { Artist = recording.Release.Artist.Name, Album = recording.Release.Title }).SingleOrDefaultAsync(cancellationToken);
                    if (album is null) continue;
                    var alternative = await albumFallback.ResolveAsync(album.Artist ?? string.Empty, album.Album ?? string.Empty, cancellationToken);
                    if (alternative is null) continue;
                    var result = await assets.GetOrCreateAsync(alternative.AbsoluteUri, cancellationToken);
                    logger.LogInformation("Recovered cover for {EntityKind}/{EntityIdentifier} through the album artwork fallback.", job.EntityKind, job.EntityIdentifier);
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception fallbackFailure) when (fallbackFailure is IOException or HttpRequestException or OperationCanceledException or JsonException)
                {
                    failures.Add(fallbackFailure);
                    logger.LogWarning(fallbackFailure, "Album artwork fallback failed for {EntityKind}/{EntityIdentifier}; remaining sources will be tried.", job.EntityKind, job.EntityIdentifier);
                }
            }
        }
        throw new AggregateException("No cover source candidate produced a valid asset.", failures);
    }
    private static bool IsArchiveSource(string source) => Uri.TryCreate(source, UriKind.Absolute, out var address)
        && (address.Host.Equals("coverartarchive.org", StringComparison.OrdinalIgnoreCase)
            || address.Host.Equals("archive.org", StringComparison.OrdinalIgnoreCase)
            || address.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase));
    public static TimeSpan RetryDelay(int attempts, CoverAssetOptions options) => TimeSpan.FromTicks((long)Math.Min(options.MaximumRetryDelay.Ticks,
        options.InitialRetryDelay.Ticks * Math.Pow(2, Math.Min(attempts - 1, 20))));
    public Task RepairAsync(CancellationToken cancellationToken) =>
        new CoverAssetMaintenanceService(scopes, assets, configuration, clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CoverAssetMaintenanceService>.Instance).RunOnceAsync(cancellationToken);
}
