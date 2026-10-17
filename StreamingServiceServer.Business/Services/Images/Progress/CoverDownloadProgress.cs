using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
namespace StreamingServiceServer.Business.Services.Images.Progress;

public enum CoverProgressFilter { All, Missing, Downloaded }
public static class CoverProgressPolicy
{
    public const int FirstPage = 1;
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
    public const int MaximumPage = 1000000;
    public const int MaximumSearchLength = 200;
    public const int VariantCount = 3;
    public const int CompletePercentage = 100;
}
public sealed record CoverProgressSummary(int TotalAlbums, int DownloadedAlbums, int UnavailableAlbums, int PartialAlbums)
{
    public double Percentage => TotalAlbums == 0 ? 0 : Math.Round((double)DownloadedAlbums / TotalAlbums * CoverProgressPolicy.CompletePercentage, 1);
}
public sealed record CoverProgressAlbum(Guid Id, string Title, string Artist, int DownloadedVariants, double Percentage,
    string Status, DateTimeOffset? NextAttemptAt);
public sealed record CoverProgressPage(CoverProgressSummary Summary, IReadOnlyList<CoverProgressAlbum> Albums,
    int Page, int PageSize, int FilteredAlbums);
public sealed class CoverProgressRow
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public bool FullReady { get; init; }
    public bool SmallReady { get; init; }
    public bool ThumbnailReady { get; init; }
    public bool HasSource { get; init; }
    public bool Suspended { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public CoverProgressAlbum ToAlbum(DateTimeOffset now)
    {
        var completed = (FullReady ? 1 : 0) + (SmallReady ? 1 : 0) + (ThumbnailReady ? 1 : 0);
        var status = completed == CoverProgressPolicy.VariantCount ? "downloaded"
            : Suspended ? "unavailable" : LeaseUntil > now ? "downloading" : completed > 0 ? "partial"
            : !HasSource ? "no-source" : Attempts > 0 ? "retrying" : "queued";
        return new CoverProgressAlbum(Id, Title, Artist, completed,
            Math.Round((double)completed / CoverProgressPolicy.VariantCount * CoverProgressPolicy.CompletePercentage, 1),
            status, status == "retrying" ? NextAttemptAt : null);
    }
}
public sealed class CoverDownloadProgress(StreamingDbContext database, TimeProvider clock)
{
    public async Task<CoverProgressPage> ReadAsync(int page, int pageSize, CoverProgressFilter filter, string? search, CancellationToken cancellationToken)
    {
        if (page < CoverProgressPolicy.FirstPage || page > CoverProgressPolicy.MaximumPage) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize < 1 || pageSize > CoverProgressPolicy.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (!Enum.IsDefined(filter)) throw new ArgumentOutOfRangeException(nameof(filter));
        if (search?.Length > CoverProgressPolicy.MaximumSearchLength) throw new ArgumentOutOfRangeException(nameof(search));
        var query = from release in database.Releases.AsNoTracking()
            join job in database.CoverAssetJobs.AsNoTracking().Where(job => job.EntityKind == CoverEntityKind.Release)
                on release.Id equals job.EntityIdentifier into matchingJobs
            from job in matchingJobs.DefaultIfEmpty()
            select new CoverProgressRow {
                Id = release.Id, Title = release.Title ?? string.Empty, Artist = release.Artist.Name ?? string.Empty,
                FullReady = release.LocalCover != null && release.LocalCover.Trim() != string.Empty,
                SmallReady = release.LocalSmallCover != null && release.LocalSmallCover.Trim() != string.Empty,
                ThumbnailReady = release.LocalVerySmallCover != null && release.LocalVerySmallCover.Trim() != string.Empty,
                HasSource = !string.IsNullOrWhiteSpace(release.Cover) || !string.IsNullOrWhiteSpace(release.SmallCover) || !string.IsNullOrWhiteSpace(release.VerySmallCover),
                Suspended = job != null && job.Suspended, Attempts = job == null ? 0 : job.Attempts,
                LeaseUntil = job == null ? null : job.LeaseUntil, NextAttemptAt = job == null ? null : job.NextAttemptAt };
        var summary = await query.GroupBy(row => 1).Select(group => new CoverProgressSummary(group.Count(),
            group.Count(row => row.FullReady && row.SmallReady && row.ThumbnailReady),
            group.Count(row => row.Suspended && !(row.FullReady && row.SmallReady && row.ThumbnailReady)),
            group.Count(row => (row.FullReady || row.SmallReady || row.ThumbnailReady) && !(row.FullReady && row.SmallReady && row.ThumbnailReady))))
            .SingleOrDefaultAsync(cancellationToken) ?? new CoverProgressSummary(0, 0, 0, 0);
        if (filter == CoverProgressFilter.Downloaded) query = query.Where(row => row.FullReady && row.SmallReady && row.ThumbnailReady);
        if (filter == CoverProgressFilter.Missing) query = query.Where(row => !(row.FullReady && row.SmallReady && row.ThumbnailReady));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(row => row.Title.ToLower().Contains(term) || row.Artist.ToLower().Contains(term));
        }
        var filtered = await query.CountAsync(cancellationToken);
        var lastPage = Math.Max(CoverProgressPolicy.FirstPage, (int)Math.Ceiling((double)filtered / pageSize));
        var currentPage = Math.Min(page, lastPage);
        var rows = await query.OrderBy(row => row.Title).ThenBy(row => row.Id).Skip((currentPage - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return new CoverProgressPage(summary, rows.Select(row => row.ToAlbum(now)).ToArray(), currentPage, pageSize, filtered);
    }
}
