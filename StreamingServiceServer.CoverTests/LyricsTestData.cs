using System.Text.Json;
using StreamingServiceServer.Business.Lyrics;

namespace StreamingServiceServer.CoverTests;

internal static class LyricsTestData
{
    public const string Title = "A Song & Its Echo";
    public const string Artist = "The Example Artist";
    public const string Album = "The Example Album";
    public const string OtherTitle = "Another Song";
    public const string OtherArtist = "Another Artist";
    public const string OtherAlbum = "Another Album";
    public const string Plain = "First invented line\nSecond invented line";
    public const string Synced = "[00:01.00]First invented line\n[00:05.50]Second invented line";
    public const string Lyricsfile = "version: '1.0'\nlines: []";
    public const string EmptyIssues = "[]";
    public const string InvalidJson = "{";
    public const string PrivateFailure = "Private transport details must not be stored";
    public const string NameMismatch = "name_mismatch";
    public const string ArtistMismatch = "artist_mismatch";
    public const string LengthMismatch = "length_mismatch";
    public const string NameWithWhitespace = "  A   Song & Its Echo  ";
    public const string InstrumentalJson = "{\"id\":123,\"trackName\":\"A Song & Its Echo\",\"artistName\":\"The Example Artist\",\"albumName\":\"The Example Album\",\"duration\":120,\"instrumental\":true}";
    public const string WrongContentType = "text/html";
    public const string ResponseAddress = "https://lyrics.invalid/";
    public const string ExpectedEscapedTitle = "track_name=A%20Song%20%26%20Its%20Echo";
    public const string ExpectedDuration = "duration=120";
    public const string AuthorizationHeader = "Authorization";
    public const string ProviderKeyHeader = "Lrclib-Api-Key";
    public const string ShortInterval = "00:00:00";
    public const string UnsafeLease = "00:00:01";
    public const string InvalidBatch = "0";
    public const string DesignConnection = "Host=127.0.0.1;Port=1;Database=lyrics_design_only;Username=design_only";
    public const int DurationMilliseconds = 120000;
    public const double DurationSeconds = 120;
    public const double DifferentDurationSeconds = 130;
    public const double BoundaryDurationSeconds = 122;
    public const double OutsideDurationSeconds = 122.001;
    public const long ProviderIdentifier = 123;
    public const int NoCalls = 0;
    public const int OneCall = 1;
    public const int TwoCalls = 2;
    public const int ManyAttempts = 100;
    public const int InvalidEnumValue = -1;
    public const string JobsProperty = "jobs";
    public const char OversizedResponseCharacter = 'x';
    public const int FirstIndex = 0;
    public const int NextIndex = 1;
    public static readonly Guid RecordingIdentifier = Guid.Parse("06d6063e-ecfe-4e9d-9a2b-45bb619a1254");
    public static readonly Guid OtherRecordingIdentifier = Guid.Parse("ee5ab194-e191-42d6-87b0-77a106843dd0");
    public static readonly Guid AlbumIdentifier = Guid.Parse("0b71cf4b-e077-4404-967e-4286c74decc3");
    public static readonly Guid ArtistIdentifier = Guid.Parse("1c461825-90af-4a87-98f0-61f4b3f70d48");
    public static readonly Guid CreditIdentifier = Guid.Parse("d52992a9-3667-493d-88ed-56c9ea4c8ef0");
    public static readonly Guid FirstToken = Guid.Parse("be6ce73c-c6d7-406a-8edc-5e1d5148e629");
    public static readonly Guid SecondToken = Guid.Parse("4d05ba75-945d-4ba4-a687-7a49679e9e74");
    public static readonly TimeSpan ProviderCooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetrySafetyMargin = TimeSpan.FromSeconds(1);
    public static LyricsTrack Track => new(RecordingIdentifier, Title, Artist, Album, DurationMilliseconds, false);
    public static LyricsContent Content => new(ProviderIdentifier, Plain, Synced, Lyricsfile, false);
    public static string Issues(string type, Guid? recordingIdentifier = null) => JsonSerializer.Serialize(new[]
    {
        new { recordingId = recordingIdentifier ?? RecordingIdentifier, issues = new[] { new { Type = type } } }
    });
    public static string Payload(string title = Title, string artist = Artist, string album = Album,
        double durationSeconds = DurationSeconds, string? plain = Plain, string? synced = Synced) => JsonSerializer.Serialize(new
    {
        id = ProviderIdentifier, trackName = title, artistName = artist, albumName = album, duration = durationSeconds,
        instrumental = false, plainLyrics = plain, syncedLyrics = synced, lyricsfile = Lyricsfile
    });
}

internal sealed class LyricsTestIdentifiers : ILyricsIdentifiers
{
    private readonly Queue<Guid> identifiers = new([LyricsTestData.FirstToken, LyricsTestData.SecondToken]);
    public Guid Create() => identifiers.Dequeue();
}
