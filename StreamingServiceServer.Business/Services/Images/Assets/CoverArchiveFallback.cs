using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using StreamingServiceServer.Business.Services.MusicSearch.Requests;
namespace StreamingServiceServer.Business.Services.Images.Assets;

public interface ICoverArchiveFallback
{
    Task<Uri?> ResolveAsync(Uri source, CancellationToken cancellationToken);
}
public sealed class CoverArchiveFallback(Func<HttpClient> createClient, Uri metadataRoot, TimeProvider clock,
    IMusicBrainzRequestQueue? requestQueue = null) : ICoverArchiveFallback
{
    private const string ArchiveHost = "coverartarchive.org";
    private const string ReleaseKind = "release";
    private const string GroupKind = "release-group";
    private const string IdentifierField = "id";
    private const string MetadataQuery = "?inc=release-groups&fmt=json";
    private const int MaximumCachedReleases = 1024;
    private const int MaximumMetadataBytes = 1048576;
    private const int SourceSegmentCount = 3;
    private const int ReleaseKindSegment = 0;
    private const int ReleaseIdentifierSegment = 1;
    private const int ImageTierSegment = 2;
    private const int BodyBufferBytes = 8192;
    private static readonly HashSet<string> ImageTiers = new(StringComparer.Ordinal) {
        CoverPolicy.FullSuffix.Trim('/'), CoverPolicy.SmallSuffix.Trim('/'), CoverPolicy.ThumbnailSuffix.Trim('/') };
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);
    private readonly ConcurrentDictionary<Guid, Lazy<Task<GroupLookup>>> lookups = new();
    private readonly IMusicBrainzRequestQueue metadataRequests = requestQueue ?? MusicBrainzRequestQueue.Shared;
    private sealed record GroupLookup(Guid? Identifier, DateTimeOffset ExpiresAt);
    public async Task<Uri?> ResolveAsync(Uri source, CancellationToken cancellationToken)
    {
        var segments = source.AbsolutePath.Trim('/').Split('/');
        if (source.Scheme != Uri.UriSchemeHttps || !source.Host.Equals(ArchiveHost, StringComparison.OrdinalIgnoreCase)
            || segments.Length != SourceSegmentCount || segments[ReleaseKindSegment] != ReleaseKind
            || !Guid.TryParse(segments[ReleaseIdentifierSegment], out var releaseIdentifier) || !ImageTiers.Contains(segments[ImageTierSegment])) return null;
        while (true)
        {
            TrimCompleted();
            var lookup = lookups.GetOrAdd(releaseIdentifier, identifier => new Lazy<Task<GroupLookup>>(() => LookupAsync(identifier)));
            var completion = lookup.Value;
            _ = completion.ContinueWith(completed => {
                _ = completed.Exception;
                if (completed.IsFaulted || completed.IsCanceled)
                    lookups.TryRemove(new KeyValuePair<Guid, Lazy<Task<GroupLookup>>>(releaseIdentifier, lookup));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            GroupLookup result;
            try { result = await completion.WaitAsync(cancellationToken); }
            catch
            {
                if (lookup.IsValueCreated && (lookup.Value.IsFaulted || lookup.Value.IsCanceled))
                    lookups.TryRemove(new KeyValuePair<Guid, Lazy<Task<GroupLookup>>>(releaseIdentifier, lookup));
                throw;
            }
            if (result.ExpiresAt <= clock.GetUtcNow())
            {
                lookups.TryRemove(new KeyValuePair<Guid, Lazy<Task<GroupLookup>>>(releaseIdentifier, lookup)); continue;
            }
            return result.Identifier is null ? null : new Uri(new Uri(CoverPolicy.DefaultArchiveRoot),
                GroupKind + "/" + result.Identifier.Value + "/" + segments[ImageTierSegment]);
        }
    }
    private async Task<GroupLookup> LookupAsync(Guid identifier)
    {
        using var timeout = new CancellationTokenSource(LookupTimeout, clock);
        using var client = createClient();
        using var response = await metadataRequests.SendAsync(requestCancellation => client.GetAsync(
            new Uri(metadataRoot, ReleaseKind + "/" + identifier + MetadataQuery),
            HttpCompletionOption.ResponseHeadersRead, requestCancellation), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return new GroupLookup(null, clock.GetUtcNow() + CacheLifetime);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumMetadataBytes) throw new IOException("Cover metadata exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var body = new MemoryStream();
        var buffer = new byte[BodyBufferBytes]; int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (body.Length + count > MaximumMetadataBytes) throw new IOException("Cover metadata exceeds its size limit.");
            await body.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
        Guid? group = document.RootElement.TryGetProperty(GroupKind, out var releaseGroup)
            && releaseGroup.ValueKind == JsonValueKind.Object && releaseGroup.TryGetProperty(IdentifierField, out var groupIdentifier)
            && groupIdentifier.TryGetGuid(out var parsed) && parsed != Guid.Empty ? parsed : null;
        return new GroupLookup(group, clock.GetUtcNow() + CacheLifetime);
    }
    private void TrimCompleted()
    {
        if (lookups.Count < MaximumCachedReleases) return;
        foreach (var pair in lookups.Where(pair => pair.Value.IsValueCreated && pair.Value.Value.IsCompleted).Take(lookups.Count - MaximumCachedReleases + 1))
        {
            _ = pair.Value.Value.Exception;
            lookups.TryRemove(pair);
        }
        if (lookups.Count >= MaximumCachedReleases) throw new IOException("Cover metadata lookup capacity is occupied.");
    }
}
