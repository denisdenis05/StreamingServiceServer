using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data;
namespace StreamingServiceServer.Business.Services.Images.Assets;

public sealed class CoverJobInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
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
            .Where(entry => entry.State == EntityState.Added || (entry.State == EntityState.Modified && entry.Property(nameof(Release.Cover)).IsModified))
            .Select(entry => entry.Entity is Release release ? (Kind: CoverEntityKind.Release, Identifier: release.Id, Address: release.Cover)
                : (Kind: CoverEntityKind.Recording, Identifier: ((Recording)entry.Entity).Id, Address: ((Recording)entry.Entity).Cover)).ToArray();
        foreach (var group in changes.GroupBy(change => change.Kind))
        {
            var identifiers = group.Select(change => change.Identifier).ToArray();
            var stored = await database.CoverAssetJobs.Where(job => job.EntityKind == group.Key && identifiers.Contains(job.EntityIdentifier)).ToListAsync(cancellationToken);
            foreach (var change in group)
            {
                if (string.IsNullOrWhiteSpace(change.Address)) continue;
                var job = database.CoverAssetJobs.Local.FirstOrDefault(job => job.EntityKind == change.Kind && job.EntityIdentifier == change.Identifier)
                    ?? stored.FirstOrDefault(job => job.EntityIdentifier == change.Identifier);
                if (job is null) { job = new CoverAssetJob { EntityKind = change.Kind, EntityIdentifier = change.Identifier }; database.CoverAssetJobs.Add(job); }
                Schedule(job, change.Address, clock.GetUtcNow());
            }
        }
    }
    public static void Schedule(CoverAssetJob job, string sourceAddress, DateTimeOffset now)
    {
        job.SourceAddress = sourceAddress; job.Version = Guid.NewGuid(); job.Attempts = 0;
        job.LeaseToken = null; job.LeaseUntil = null;
        job.CompletedAt = null; job.NextAttemptAt = now; job.EnqueuedAt = now;
        job.LastFailure = string.Empty;
    }
}
