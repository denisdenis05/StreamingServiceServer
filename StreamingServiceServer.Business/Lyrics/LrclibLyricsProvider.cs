using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Lyrics;

public sealed class LrclibLyricsProvider(HttpClient client, TimeProvider clock) : ILyricsProvider
{
    private const string ProviderIdentifierProperty = "id";
    private const string TitleParameter = "?track_name=";
    private const string ArtistParameter = "&artist_name=";
    private const string AlbumParameter = "&album_name=";
    private const string DurationParameter = "&duration=";
    private const string SecondsFormat = "0.###";
    private static readonly JsonSerializerOptions Serialization = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetrySafetyMargin = TimeSpan.FromSeconds(1);

    public async Task<LyricsContent?> DownloadAsync(LyricsTrack track, CancellationToken cancellationToken)
    {
        if (LyricsMatchPolicy.Rejection(track) is not null) throw new LyricsProviderException(LyricsProtocol.InvalidMetadata);
        var address = LyricsProtocol.GetRoute + TitleParameter + Uri.EscapeDataString(track.Title)
            + ArtistParameter + Uri.EscapeDataString(track.Artist) + AlbumParameter + Uri.EscapeDataString(track.Album)
            + DurationParameter + track.DurationSeconds.ToString(SecondsFormat, CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date is { } date ? date - clock.GetUtcNow() : (TimeSpan?)null);
            throw new LyricsProviderException(LyricsProtocol.ProviderFailure,
                delay is null ? null : (delay > TimeSpan.Zero ? delay.Value : TimeSpan.Zero) + RetrySafetyMargin);
        }
        if (response.Content.Headers.ContentType?.MediaType != LyricsProtocol.JsonMediaType)
            throw new LyricsProviderException(LyricsProtocol.InvalidResponse);
        await response.Content.LoadIntoBufferAsync(LyricsProtocol.MaximumResponseBytes, cancellationToken);
        var record = JsonSerializer.Deserialize<ProviderRecord>(await response.Content.ReadAsStringAsync(cancellationToken), Serialization)
            ?? throw new LyricsProviderException(LyricsProtocol.InvalidResponse);
        if (!LyricsMatchPolicy.Matches(track, record.TrackName, record.ArtistName, record.AlbumName, record.Duration))
            throw new LyricsProviderException(LyricsProtocol.UnmatchedRecording);
        if (record.Identifier < LyricsProtocol.MinimumProviderIdentifier || (!record.Instrumental
            && string.IsNullOrWhiteSpace(record.PlainLyrics) && string.IsNullOrWhiteSpace(record.SyncedLyrics)))
            throw new LyricsProviderException(LyricsProtocol.InvalidResponse);
        return new LyricsContent(record.Identifier, record.Instrumental ? null : record.PlainLyrics,
            record.Instrumental ? null : record.SyncedLyrics, record.Lyricsfile, record.Instrumental);
    }

    private sealed record ProviderRecord([property: JsonPropertyName(ProviderIdentifierProperty)] long Identifier, string? TrackName, string? ArtistName, string? AlbumName, double Duration,
        bool Instrumental, string? PlainLyrics, string? SyncedLyrics, string? Lyricsfile);
}
