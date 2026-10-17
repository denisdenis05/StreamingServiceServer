using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Background;

public sealed class CoverMetadataRefreshService(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<CoverMetadataRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan ProviderInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessOneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception failure)
            {
                logger.LogWarning(failure, "Album cover metadata refresh paused; its persisted checkpoint will be retried.");
                await Task.Delay(RetryInterval, clock, stoppingToken);
            }
            await Task.Delay(ProviderInterval, clock, stoppingToken);
        }
    }

    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var operation = await database.CoverMetadataRefreshes.AsNoTracking().SingleOrDefaultAsync(
            refresh => refresh.Id == CoverMetadataRefresh.OperationKey && refresh.Pending, cancellationToken);
        if (operation is null) return false;
        var release = await database.Releases.AsNoTracking()
            .Where(release => operation.Cursor == null || release.Id.CompareTo(operation.Cursor.Value) > 0)
            .OrderBy(release => release.Id).Select(release => new { release.Id, release.Cover, release.SmallCover, release.VerySmallCover })
            .FirstOrDefaultAsync(cancellationToken);
        if (release is null)
        {
            await database.CoverMetadataRefreshes.Where(refresh => refresh.Id == operation.Id && refresh.Version == operation.Version)
                .ExecuteUpdateAsync(update => update.SetProperty(refresh => refresh.Pending, false)
                    .SetProperty(refresh => refresh.CompletedAt, clock.GetUtcNow()).SetProperty(refresh => refresh.Version, Guid.NewGuid()), cancellationToken);
            return false;
        }
        var metadata = scope.ServiceProvider.GetRequiredService<IExternalMusicSearchService>();
        var images = scope.ServiceProvider.GetRequiredService<IImageService>();
        ICollection<ReleaseDto> externalReleases;
        try { externalReleases = await metadata.GetReleasesFromId(release.Id).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            await database.CoverMetadataRefreshes.Where(refresh => refresh.Id == operation.Id && refresh.Version == operation.Version)
                .ExecuteUpdateAsync(update => update.SetProperty(refresh => refresh.LastFailure, failure.GetType().Name), cancellationToken);
            throw;
        }
        var groupIdentifier = externalReleases.FirstOrDefault()?.ReleaseGroup?.Id;
        var covers = groupIdentifier is not null && groupIdentifier != Guid.Empty
            ? images.GetAllAlbumCovers(groupIdentifier.Value) : images.GetAlbumCover(release.Id);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var checkpointed = await database.CoverMetadataRefreshes.Where(refresh => refresh.Id == operation.Id && refresh.Version == operation.Version && refresh.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(refresh => refresh.Cursor, release.Id)
                .SetProperty(refresh => refresh.CompletedReleases, refresh => refresh.CompletedReleases + 1)
                .SetProperty(refresh => refresh.Version, Guid.NewGuid()).SetProperty(refresh => refresh.LastFailure, string.Empty), cancellationToken);
        if (checkpointed == 0) { await transaction.RollbackAsync(cancellationToken); return false; }
        var currentRelease = await database.Releases.FromSqlInterpolated($"""
            SELECT * FROM "Releases" WHERE "Id" = {release.Id}
                AND COALESCE("Cover", {string.Empty}) = {release.Cover ?? string.Empty}
                AND COALESCE("SmallCover", {string.Empty}) = {release.SmallCover ?? string.Empty}
                AND COALESCE("VerySmallCover", {string.Empty}) = {release.VerySmallCover ?? string.Empty}
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (currentRelease is not null)
        {
            currentRelease.Cover = covers.Cover ?? string.Empty;
            currentRelease.SmallCover = covers.SmallCover;
            currentRelease.VerySmallCover = covers.VerySmallCover;
            if (release.Cover != covers.Cover || release.SmallCover != covers.SmallCover || release.VerySmallCover != covers.VerySmallCover)
                await ReplaceInheritedRecordingSourcesAsync(database, release.Id,
                    CoverSourceSet.Capture(release.Cover, release.SmallCover, release.VerySmallCover),
                    CoverSourceSet.Capture(covers.Cover, covers.SmallCover, covers.VerySmallCover), clock.GetUtcNow(), cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
    private static Task<int> ReplaceInheritedRecordingSourcesAsync(StreamingDbContext database, Guid releaseIdentifier,
        CoverSourceSet previous, CoverSourceSet replacement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var revision = Guid.NewGuid();
        return database.Database.ExecuteSqlInterpolatedAsync($"""
            WITH inherited_recordings AS (
                UPDATE "Recordings" SET
                    "Cover" = {replacement.Full}, "SmallCover" = {replacement.Small}, "VerySmallCover" = {replacement.Thumbnail},
                    "LocalCover" = {string.Empty}, "LocalSmallCover" = {string.Empty}, "LocalVerySmallCover" = {string.Empty},
                    "OptimizedCover" = {string.Empty}, "OptimizedSmallCover" = {string.Empty}, "OptimizedVerySmallCover" = {string.Empty}
                WHERE "ReleaseId" = {releaseIdentifier}
                    AND COALESCE("Cover", {string.Empty}) = {previous.Full}
                    AND COALESCE("SmallCover", {string.Empty}) = {previous.Small}
                    AND COALESCE("VerySmallCover", {string.Empty}) = {previous.Thumbnail}
                RETURNING "Id"
            )
            UPDATE "CoverAssetJobs" AS job SET
                "SourceAddress" = {replacement.PrimaryAddress}, "SourceRevision" = {replacement.Revision},
                "InheritedSourceRevision" = {string.Empty}, "Version" = {revision},
                "Priority" = {(int)CoverJobPriority.UserChange}, "Attempts" = {0}, "Suspended" = {false},
                "CompletedAt" = NULL, "LeaseToken" = NULL, "LeaseUntil" = NULL,
                "NextAttemptAt" = {now}, "EnqueuedAt" = {now}, "LastFailure" = {string.Empty}
            FROM inherited_recordings AS recording
            WHERE job."EntityKind" = {(int)CoverEntityKind.Recording} AND job."EntityIdentifier" = recording."Id"
            """, cancellationToken);
    }

}
