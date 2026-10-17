using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data;
namespace StreamingServiceServer.Business.Services.Images.Assets;

public sealed class CoverJobInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    private static readonly string[] SourceProperties = [nameof(Release.Cover), nameof(Release.SmallCover), nameof(Release.VerySmallCover)];
    private static readonly string[] DerivativeProperties = [nameof(Release.LocalCover), nameof(Release.LocalSmallCover),
        nameof(Release.LocalVerySmallCover), nameof(Release.OptimizedCover), nameof(Release.OptimizedSmallCover), nameof(Release.OptimizedVerySmallCover)];
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    { EnqueueAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult(); return result; }
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { await EnqueueAsync(eventData.Context, cancellationToken); return result; }
    private async Task EnqueueAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is not StreamingDbContext database) return;
        database.ChangeTracker.DetectChanges();
        var changes = database.ChangeTracker.Entries().Where(entry => entry.Entity is Release or Recording)
            .Where(entry => entry.State == EntityState.Added || (entry.State == EntityState.Modified
                && SourceProperties.Any(property => entry.Property(property).IsModified)))
            .Select(entry => (Entry: entry, Kind: entry.Entity is Release ? CoverEntityKind.Release : CoverEntityKind.Recording,
                Identifier: (Guid)entry.Property(nameof(Release.Id)).CurrentValue!, Sources: Sources(entry))).ToArray();
        foreach (var group in changes.GroupBy(change => change.Kind))
        {
            var identifiers = group.Select(change => change.Identifier).ToArray();
            var stored = await database.CoverAssetJobs.Where(job => job.EntityKind == group.Key && identifiers.Contains(job.EntityIdentifier))
                .ToDictionaryAsync(job => job.EntityIdentifier, cancellationToken);
            foreach (var local in database.CoverAssetJobs.Local.Where(job => job.EntityKind == group.Key)) stored[local.EntityIdentifier] = local;
            foreach (var change in group)
            {
                stored.TryGetValue(change.Identifier, out var job);
                InvalidatePreviousDerivatives(change.Entry, change.Sources);
                if (change.Sources.Candidates.Count == 0)
                {
                    if (job is not null) database.CoverAssetJobs.Remove(job);
                    continue;
                }
                if (job is null)
                {
                    job = new CoverAssetJob { EntityKind = change.Kind, EntityIdentifier = change.Identifier };
                    database.CoverAssetJobs.Add(job); stored[change.Identifier] = job;
                }
                var inheritedRevision = job.InheritedSourceRevision;
                if (change.Kind == CoverEntityKind.Release && change.Entry.State == EntityState.Modified
                    && (job.CompletedAt is not null || string.IsNullOrEmpty(inheritedRevision)))
                    inheritedRevision = CoverSourceSet.Capture((string?)change.Entry.Property(nameof(Release.Cover)).OriginalValue,
                        (string?)change.Entry.Property(nameof(Release.SmallCover)).OriginalValue,
                        (string?)change.Entry.Property(nameof(Release.VerySmallCover)).OriginalValue).Revision;
                Schedule(job, change.Sources, clock.GetUtcNow());
                job.InheritedSourceRevision = inheritedRevision;
            }
        }
    }
    private static CoverSourceSet Sources(EntityEntry entry) => CoverSourceSet.Capture(
        (string?)entry.Property(nameof(Release.Cover)).CurrentValue, (string?)entry.Property(nameof(Release.SmallCover)).CurrentValue,
        (string?)entry.Property(nameof(Release.VerySmallCover)).CurrentValue);
    private static void InvalidatePreviousDerivatives(EntityEntry entry, CoverSourceSet sources)
    {
        if (sources.Candidates.Count > 0 && entry.State == EntityState.Added) return;
        foreach (var propertyName in DerivativeProperties)
        {
            var property = entry.Property(propertyName);
            if (sources.Candidates.Count > 0 && property.IsModified) continue;
            property.CurrentValue = string.Empty;
        }
    }
    public static void Schedule(CoverAssetJob job, string sourceAddress, DateTimeOffset now,
        CoverJobPriority priority = CoverJobPriority.UserChange) => Schedule(job, CoverSourceSet.Capture(sourceAddress, null, null), now, priority);
    public static void Schedule(CoverAssetJob job, CoverSourceSet sources, DateTimeOffset now,
        CoverJobPriority priority = CoverJobPriority.UserChange)
    {
        job.SourceAddress = sources.PrimaryAddress; job.SourceRevision = sources.Revision; job.Priority = priority;
        job.Version = Guid.NewGuid(); job.Attempts = 0; job.Suspended = false;
        job.LeaseToken = null; job.LeaseUntil = null;
        job.CompletedAt = null; job.NextAttemptAt = now; job.EnqueuedAt = now;
        job.LastFailure = string.Empty;
    }
}
