using static StreamingServiceServer.Business.Lyrics.LyricsTrackMatchProtocol;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;

namespace StreamingServiceServer.Business.Lyrics;

public sealed class LyricsCatalog(StreamingDbContext database) : ILyricsCatalog
{
    private const int NoArtistCredits = 0;
    private const string ArtistSeparator = " & ";

    public async Task<LyricsTrack?> ReadAsync(Guid recordingIdentifier, CancellationToken cancellationToken)
    {
        var recording = await database.Recordings.AsNoTracking().Where(recording => recording.Id == recordingIdentifier)
            .Select(recording => new { recording.Title, recording.Length, Album = recording.Release.Title,
                AlbumArtist = recording.Release.Artist.Name, AlbumIdentifier = recording.Release.Id,
                Artists = recording.ArtistCredit.OrderBy(credit => credit.Id).Select(credit => credit.Name ?? credit.Artist!.Name).ToArray() })
            .SingleOrDefaultAsync(cancellationToken);
        if (recording is null) return null;
        var issues = await database.AlbumAnalysisResults.AsNoTracking().Where(analysis => analysis.AlbumId == recording.AlbumIdentifier)
            .Select(analysis => analysis.TrackMatchIssuesJson).SingleOrDefaultAsync(cancellationToken);
        var artist = recording.Artists.Length == NoArtistCredits ? recording.AlbumArtist
            : string.Join(ArtistSeparator, recording.Artists);
        return new LyricsTrack(recordingIdentifier, recording.Title, artist ?? string.Empty, recording.Album ?? string.Empty,
            recording.Length, HasNamingIssue(issues, recordingIdentifier));
    }

    public static bool HasNamingIssue(string? issues, Guid recordingIdentifier)
    {
        if (string.IsNullOrWhiteSpace(issues)) return false;
        try
        {
            using var document = JsonDocument.Parse(issues);
            foreach (var track in document.RootElement.EnumerateArray())
            {
                if (!track.TryGetProperty(RecordingIdentifierProperty, out var identifier) || !identifier.TryGetGuid(out var parsed)) return true;
                if (parsed != recordingIdentifier) continue;
                if (!track.TryGetProperty(IssuesProperty, out var entries)) return true;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty(IssueTypeProperty, out var type)
                        && !entry.TryGetProperty(CamelCaseIssueTypeProperty, out type)) return true;
                    if (type.GetString() is TitleMismatch or ArtistMismatch) return true;
                }
            }
            return false;
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException) { return true; }
    }
}
