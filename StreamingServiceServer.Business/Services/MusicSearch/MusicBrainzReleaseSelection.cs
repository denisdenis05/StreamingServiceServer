using Microsoft.Extensions.Caching.Memory;
using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public partial class MusicBrainzService
{
    private const int ReleaseSelectionPageSize = 100;
    private const int MaximumSelectionGroups = 8;
    private static readonly TimeSpan SelectionCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly MemoryCache SelectionCache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private static readonly SemaphoreSlim SelectionCacheGate = new(1, 1);

    public async Task<IReadOnlyList<ReleaseGroupSelection>> SearchReleaseSelectionsAsync(string title, string? artist,
        CancellationToken cancellationToken)
    {
        var key = ("release-selection-search", _baseUrl, title.Trim().ToLowerInvariant(), artist?.Trim().ToLowerInvariant());
        return await ReadSelectionCacheAsync(key, async () =>
        {
            var query = $"release:\"{EscapeSelectionQuery(title)}\"";
            if (!string.IsNullOrWhiteSpace(artist)) query += $" AND artist:\"{EscapeSelectionQuery(artist)}\"";
            var address = $"{_baseUrl}release?query={Uri.EscapeDataString(query)}&limit={ReleaseSelectionPageSize}&fmt=json";
            var response = await GetJsonAsync<MusicBrainzSearchResponse>(address, cancellationToken)
                ?? throw new HttpRequestException("MusicBrainz release search is unavailable. Try again shortly.");
            return (IReadOnlyList<ReleaseGroupSelection>)(response.Releases ?? [])
                .Where(release => release.ReleaseGroup is { Id: var identifier } && identifier != Guid.Empty)
                .GroupBy(release => release.ReleaseGroup!.Id)
                .Take(MaximumSelectionGroups)
                .Select(group =>
                {
                    var release = group.OrderByDescending(candidate =>
                            string.Equals(candidate.Title, candidate.ReleaseGroup!.Title, StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(candidate => candidate.Score).First();
                    var selection = ToReleaseSelection(release, group.Key);
                    return new ReleaseGroupSelection(group.Key, release.ReleaseGroup!.Title ?? selection.Title,
                        selection.Artist, selection.GroupType, selection);
                }).ToArray();
        }, cancellationToken);
    }

    public Task<ReleaseSelectionPage> BrowseReleaseSelectionsAsync(Guid groupId, int offset, CancellationToken cancellationToken)
    {
        if (groupId == Guid.Empty || offset < 0 || offset % ReleaseSelectionPageSize != 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var key = ("release-selection-group", _baseUrl, groupId, offset);
        return ReadSelectionCacheAsync(key, async () =>
        {
            var address = $"{_baseUrl}release?release-group={groupId}&inc=artist-credits+media+release-groups&limit={ReleaseSelectionPageSize}&offset={offset}&fmt=json";
            var response = await GetJsonAsync<MusicBrainzSearchResponse>(address, cancellationToken)
                ?? throw new HttpRequestException("MusicBrainz releases are unavailable. Try again shortly.");
            var releases = response.Releases ?? [];
            var nextOffset = offset + releases.Count;
            var hasMore = response.ReleaseCount is { } total
                ? nextOffset < total && releases.Count > 0 : releases.Count == ReleaseSelectionPageSize;
            return new ReleaseSelectionPage(releases.Select(release => ToReleaseSelection(release, groupId)).ToArray(),
                hasMore ? nextOffset : null);
        }, cancellationToken);
    }

    private static ReleaseSelection ToReleaseSelection(ReleaseDto release, Guid groupId)
    {
        var media = release.Media?.ToArray() ?? [];
        var count = release.TrackCount ?? (media.Length > 0 && media.All(medium => medium.TrackCount.HasValue)
            ? media.Sum(medium => medium.TrackCount!.Value) : (int?)null);
        var artist = string.Concat((release.ArtistCredit ?? []).Select(credit =>
            (credit.Name ?? credit.Artist?.Name ?? string.Empty) + credit.JoinPhrase));
        return new ReleaseSelection(release.Id, release.Title ?? string.Empty, artist, groupId,
            release.ReleaseGroup?.PrimaryType ?? string.Empty, count, release.Date ?? string.Empty,
            release.Country ?? string.Empty, string.Join(" / ", media.Select(medium => medium.Format)
                .Where(format => !string.IsNullOrWhiteSpace(format)).Distinct()), release.Disambiguation ?? string.Empty);
    }

    private static string EscapeSelectionQuery(string value) => value.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task<T> ReadSelectionCacheAsync<T>(object key, Func<Task<T>> load, CancellationToken cancellationToken)
    {
        if (SelectionCache.TryGetValue(key, out T? cached)) return cached!;
        await SelectionCacheGate.WaitAsync(cancellationToken);
        try
        {
            if (SelectionCache.TryGetValue(key, out cached)) return cached!;
            var value = await load();
            SelectionCache.Set(key, value, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = SelectionCacheLifetime,
                Size = 1
            });
            return value;
        }
        finally { SelectionCacheGate.Release(); }
    }
}
