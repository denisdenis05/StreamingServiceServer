using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Services.Images.Playlists;

public sealed class PlaylistArtworkInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        InvalidateAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await InvalidateAsync(eventData.Context, cancellationToken);
        return result;
    }
    private static async Task InvalidateAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is not StreamingDbContext database) return;
        var changed = database.ChangeTracker.Entries<PlaylistRecording>().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity.PlaylistId).Distinct().ToArray();
        foreach (var identifier in changed)
        {
            var tracked = database.ChangeTracker.Entries<Playlist>().FirstOrDefault(entry => entry.Entity.Id == identifier);
            if (tracked?.State == EntityState.Deleted) continue;
            var playlist = tracked?.Entity ?? await database.Playlists.SingleOrDefaultAsync(playlist => playlist.Id == identifier, cancellationToken);
            if (playlist is null) continue;
            playlist.ArtworkVersion = Guid.NewGuid();
            if (string.IsNullOrEmpty(playlist.Cover) || playlist.Cover.Contains(PlaylistCollageRenderer.GeneratedPrefix, StringComparison.Ordinal))
            {
                playlist.Cover = string.Empty;
                if (database.Entry(playlist).State != EntityState.Added) database.Entry(playlist).Property(current => current.Cover).IsModified = true;
            }
        }
    }
}
