using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace StreamingServiceServer.Business.Services.Images.Assets;

public interface IAlbumCoverFallback
{
    Task<Uri?> ResolveAsync(string artist, string album, CancellationToken cancellationToken);
}

public sealed class DeezerCoverFallback(Func<HttpClient> createClient, TimeProvider clock) : IAlbumCoverFallback, IDisposable
{
    private const int MaximumResponseBytes = 1048576;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly SemaphoreSlim requests = new(1);
    private DateTimeOffset nextRequest;
    private sealed record Lookup(Uri? Address);

    public async Task<Uri?> ResolveAsync(string artist, string album, CancellationToken cancellationToken)
    {
        var normalizedArtist = Normalize(artist);
        var normalizedAlbum = Normalize(album);
        if (normalizedArtist.Length == 0 || normalizedAlbum.Length == 0) return null;
        var key = (normalizedArtist, normalizedAlbum);
        if (cache.TryGetValue(key, out Lookup? cached)) return cached!.Address;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await requests.WaitAsync(timeout.Token);
        try
        {
            if (cache.TryGetValue(key, out cached)) return cached!.Address;
            var delay = nextRequest - clock.GetUtcNow();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, timeout.Token);
            nextRequest = clock.GetUtcNow() + TimeSpan.FromSeconds(1);
            using var client = createClient();
            var query = Uri.EscapeDataString(artist + " " + album);
            using var response = await client.GetAsync("https://api.deezer.com/search/album?q=" + query + "&limit=25",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new IOException("Album artwork lookup is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (body.Length + count > MaximumResponseBytes) throw new IOException("Album artwork lookup is too large.");
                body.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Album artwork provider returned an invalid response.");
            if (document.RootElement.TryGetProperty("error", out _)) throw new IOException("Album artwork provider returned an error.");
            if (!document.RootElement.TryGetProperty("data", out var albums) || albums.ValueKind != JsonValueKind.Array)
                throw new IOException("Album artwork provider returned an invalid response.");
            Uri? address = null;
            foreach (var candidate in albums.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object
                    || !candidate.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String
                    || Normalize(title.GetString()!) != normalizedAlbum
                    || !candidate.TryGetProperty("artist", out var performer) || performer.ValueKind != JsonValueKind.Object
                    || !performer.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || Normalize(name.GetString()!) != normalizedArtist
                    || !candidate.TryGetProperty("cover_xl", out var cover) || cover.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(cover.GetString(), UriKind.Absolute, out var image)
                    || image.Scheme != Uri.UriSchemeHttps || !image.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrEmpty(image.UserInfo) || !image.IsDefaultPort) continue;
                address = image;
                break;
            }
            cache.Set(key, new Lookup(address), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) });
            return address;
        }
        finally { requests.Release(); }
    }

    private static string Normalize(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormKC)
        .ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public void Dispose() { cache.Dispose(); requests.Dispose(); }
}
