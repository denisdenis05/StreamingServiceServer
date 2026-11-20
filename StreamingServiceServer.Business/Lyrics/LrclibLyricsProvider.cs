using System.Globalization;
using System.Net;
using System.Text.Json;

namespace StreamingServiceServer.Business.Lyrics;

public sealed class LrclibLyricsProvider(HttpClient client, TimeProvider clock, ILyricsRequestDelay requestDelay) : ILyricsProvider
{
    private const string TitleParameter = "?track_name=";
    private const string ArtistParameter = "&artist_name=";
    private const string AlbumParameter = "&album_name=";
    private const string DurationParameter = "&duration=";
    private const string SecondsFormat = "0.###";
    private static readonly JsonSerializerOptions Serialization = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetrySafetyMargin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RateLimitFallbackDelay = TimeSpan.FromMinutes(1);

    public async Task<LyricsContent?> DownloadAsync(LyricsTrack track, CancellationToken cancellationToken)
    {
        if (LyricsMatchPolicy.Rejection(track) is not null) throw new LyricsProviderException(LyricsProtocol.InvalidMetadata);
        var metadata = TitleParameter + Uri.EscapeDataString(track.Title) + ArtistParameter + Uri.EscapeDataString(track.Artist);
        var address = LyricsProtocol.GetRoute + metadata + AlbumParameter + Uri.EscapeDataString(track.Album)
            + DurationParameter + track.DurationSeconds.ToString(SecondsFormat, CultureInfo.InvariantCulture);
        var exactBody = await ReadAsync(address, cancellationToken);
        if (exactBody is not null)
        {
            var exactRecord = Deserialize<LyricsProviderRecord>(exactBody);
            if (!exactRecord.HasContent) throw new LyricsProviderException(LyricsProtocol.InvalidResponse);
            if (LyricsMatchPolicy.Matches(track, exactRecord.TrackName, exactRecord.ArtistName, exactRecord.AlbumName, exactRecord.Duration))
                return exactRecord.Content;
        }
        await requestDelay.WaitAsync(cancellationToken);
        var searchMetadata = TitleParameter + Uri.EscapeDataString(LyricsMatchPolicy.SearchName(track.Title))
            + ArtistParameter + Uri.EscapeDataString(LyricsMatchPolicy.SearchName(track.Artist));
        var searchBody = await ReadAsync(LyricsProtocol.SearchRoute + searchMetadata, cancellationToken);
        if (searchBody is null) return null;
        var records = Deserialize<LyricsProviderRecord?[]>(searchBody);
        if (records.Length > LyricsProtocol.MaximumProviderSearchResults || records.Any(record => record is null))
            throw new LyricsProviderException(LyricsProtocol.InvalidResponse);
        return LyricsCandidateSelector.Select(track, records.Select(record => record!).ToArray());
    }

    private async Task<string?> ReadAsync(string address, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date is { } date ? date - clock.GetUtcNow()
                : response.StatusCode == HttpStatusCode.TooManyRequests ? RateLimitFallbackDelay : (TimeSpan?)null);
            throw new LyricsProviderException(LyricsProtocol.ProviderFailure,
                delay is null ? null : (delay > TimeSpan.Zero ? delay.Value : TimeSpan.Zero) + RetrySafetyMargin);
        }
        if (response.Content.Headers.ContentType?.MediaType != LyricsProtocol.JsonMediaType)
            throw new LyricsProviderException(LyricsProtocol.InvalidResponse);
        try { await response.Content.LoadIntoBufferAsync(LyricsProtocol.MaximumResponseBytes, cancellationToken); }
        catch (HttpRequestException failure) when (failure.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            throw new LyricsProviderException(LyricsProtocol.InvalidResponse);
        }
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static TResponse Deserialize<TResponse>(string body) where TResponse : class
    {
        try { return JsonSerializer.Deserialize<TResponse>(body, Serialization) ?? throw new LyricsProviderException(LyricsProtocol.InvalidResponse); }
        catch (JsonException) { throw new LyricsProviderException(LyricsProtocol.InvalidResponse); }
    }
}
