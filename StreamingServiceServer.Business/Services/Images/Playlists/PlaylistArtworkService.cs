using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Data;

namespace StreamingServiceServer.Business.Services.Images.Playlists;

public interface IPlaylistArtworkService
{
    Task<string> RefreshAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken);
    Task<string> PreviewAsync(IReadOnlyList<string?> covers, CancellationToken cancellationToken);
}

public sealed class PlaylistArtworkService(StreamingDbContext database, IPlaylistCollageRenderer renderer,
    IOptions<CoverAssetOptions> options, TimeProvider clock) : IPlaylistArtworkService
{
    private const string TemporaryExtension = ".partial";
    private static readonly TimeSpan ObsoleteRetention = TimeSpan.FromDays(7);
    public async Task<string> RefreshAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken)
    {
        var playlist = await database.Playlists.AsNoTracking().SingleOrDefaultAsync(playlist => playlist.Id == playlistId && playlist.OwnerId == userId, cancellationToken);
        if (playlist is null) throw new KeyNotFoundException("Playlist not found.");
        if (!string.IsNullOrEmpty(playlist.Cover) && !playlist.Cover.Contains(PlaylistCollageRenderer.GeneratedPrefix, StringComparison.Ordinal)) return playlist.Cover;
        var rows = await (from entry in database.PlaylistRecordings.AsNoTracking()
            where entry.PlaylistId == playlistId
            join recording in database.Recordings.AsNoTracking() on entry.RecordingId equals recording.Id into recordings
            from recording in recordings.DefaultIfEmpty()
            orderby entry.Order, entry.RecordingId
            select new { entry.RecordingId, Cover = recording == null ? entry.Cover : recording.LocalCover,
                ReleaseCover = recording == null ? string.Empty : recording.Release.LocalCover }).Take(PlaylistCollageRenderer.TileCount).ToArrayAsync(cancellationToken);
        var sources = rows.Select(row => Resolve(row.Cover) ?? Resolve(row.ReleaseCover)).Where(path => path is not null).Cast<string>().ToArray();
        if (rows.Length == 0)
        {
            await database.Playlists.Where(current => current.Id == playlistId && current.OwnerId == userId && current.Cover == playlist.Cover && current.ArtworkVersion == playlist.ArtworkVersion)
                .ExecuteUpdateAsync(update => update.SetProperty(current => current.Cover, string.Empty), cancellationToken);
            return string.Empty;
        }
        if (sources.Length == 0) return playlist.Cover;
        var fingerprint = PlaylistCollageRenderer.PolicyVersion.ToString() + playlist.ArtworkVersion + playlistId + string.Join(string.Empty, rows.Select(row => row.RecordingId.ToString()))
            + string.Join(string.Empty, sources.Select(source => Path.GetFileName(source) + new FileInfo(source).Length + File.GetLastWriteTimeUtc(source).Ticks));
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        var address = await RenderAsync(sources, identity, cancellationToken);
        if (address.Length == 0) return string.Empty;
        var currentIds = await database.PlaylistRecordings.AsNoTracking().Where(entry => entry.PlaylistId == playlistId)
            .OrderBy(entry => entry.Order).ThenBy(entry => entry.RecordingId).Select(entry => entry.RecordingId).Take(PlaylistCollageRenderer.TileCount).ToArrayAsync(cancellationToken);
        if (!currentIds.SequenceEqual(rows.Select(row => row.RecordingId))) return await CurrentCoverAsync(userId, playlistId, cancellationToken);
        var changed = await database.Playlists.Where(current => current.Id == playlistId && current.OwnerId == userId && current.Cover == playlist.Cover && current.ArtworkVersion == playlist.ArtworkVersion)
            .ExecuteUpdateAsync(update => update.SetProperty(current => current.Cover, address), cancellationToken);
        if (changed == 0) return await CurrentCoverAsync(userId, playlistId, cancellationToken);
        await PruneAsync(cancellationToken);
        return address;
    }

    public async Task<string> PreviewAsync(IReadOnlyList<string?> covers, CancellationToken cancellationToken)
    {
        var sources = covers.Take(PlaylistCollageRenderer.TileCount).Select(Resolve).Where(path => path is not null).Cast<string>().ToArray();
        if (sources.Length == 0) return string.Empty;
        var fingerprint = PlaylistCollageRenderer.PolicyVersion + string.Join(string.Empty,
            sources.Select(source => Path.GetFileName(source) + new FileInfo(source).Length + File.GetLastWriteTimeUtc(source).Ticks));
        var identity = "preview-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        return await RenderAsync(sources, identity, cancellationToken);
    }

    private async Task<string> RenderAsync(IReadOnlyList<string> sources, string identity, CancellationToken cancellationToken)
    {
        var filename = PlaylistCollageRenderer.GeneratedPrefix + identity + PlaylistCollageRenderer.ImageExtension;
        var output = Path.Combine(options.Value.Path, filename);
        Directory.CreateDirectory(options.Value.Path);
        if (!File.Exists(output))
        {
            if (new DriveInfo(options.Value.Path).AvailableFreeSpace < options.Value.MinimumFreeBytes + options.Value.MaximumOutputBytes) return string.Empty;
            var temporary = output + Guid.NewGuid().ToString("N") + TemporaryExtension;
            try
            {
                if (!await renderer.RenderAsync(sources, temporary, cancellationToken)) return string.Empty;
                File.Move(temporary, output, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        return options.Value.Domain.TrimEnd('/') + "/" + options.Value.DomainExtension.Trim('/') + "/" + filename;
    }

    private async Task<string> CurrentCoverAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken) =>
        await database.Playlists.AsNoTracking().Where(playlist => playlist.Id == playlistId && playlist.OwnerId == userId)
            .Select(playlist => playlist.Cover).SingleOrDefaultAsync(cancellationToken) ?? string.Empty;

    private string? Resolve(string? cover)
    {
        if (string.IsNullOrWhiteSpace(cover)) return null;
        var filename = Uri.TryCreate(cover, UriKind.Absolute, out var address) ? Uri.UnescapeDataString(address.Segments.Last()) : cover;
        if (Path.GetFileName(filename) != filename || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var path = Path.Combine(options.Value.Path, filename);
        return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 ? path : null;
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        const int PruneBatch = 8;
        foreach (var path in Directory.EnumerateFiles(options.Value.Path, PlaylistCollageRenderer.GeneratedPrefix + "*" + PlaylistCollageRenderer.ImageExtension)
            .Where(path => File.GetLastWriteTimeUtc(path) < clock.GetUtcNow().UtcDateTime - ObsoleteRetention).Take(PruneBatch))
        {
            var filename = Path.GetFileName(path);
            if (!await database.Playlists.AsNoTracking().AnyAsync(playlist => playlist.Cover.EndsWith(filename), cancellationToken)) File.Delete(path);
        }
    }
}
