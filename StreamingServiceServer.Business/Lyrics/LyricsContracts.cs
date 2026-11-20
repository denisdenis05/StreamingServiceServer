using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Lyrics;

public static class LyricsProtocol
{
    public const string Provider = "lrclib";
    public const string BaseAddress = "https://lrclib.net/";
    public const string UserAgent = "StreamingServiceServer/1.0 (https://github.com/denisdenis05/StreamingServiceServer)";
    public const string GetRoute = "api/get";
    public const string SearchRoute = "api/search";
    public const int MaximumProviderSearchResults = 100;
    public const string InvalidMetadata = "invalid_metadata";
    public const string NamingIssue = "naming_issue";
    public const string UnmatchedRecording = "unmatched_recording";
    public const string AmbiguousRecording = "ambiguous_recording";
    public const string MissingLyrics = "lyrics_not_found";
    public const string ProviderFailure = "provider_failure";
    public const string ProviderTimeout = "provider_timeout";
    public const string ChangedMetadata = "metadata_changed";
    public const string InvalidResponse = "invalid_response";
    public const int MillisecondsPerSecond = 1000;
    public const int MaximumResponseBytes = 1048576;
    public const int InitialAttempts = 0;
    public const int UnknownDurationMilliseconds = 0;
    public const int MinimumDiscoveryBatchSize = 1;
    public const long MinimumProviderIdentifier = 1;
    public const int AttemptIncrement = 1;
    public const int NoRowsChanged = 0;
    public const int SingleWorker = 1;
    public const int RetryMultiplier = 2;
    public const int MaximumRetryExponent = 12;
    public const int FirstRetryExponent = 0;
    public const double DurationToleranceSeconds = 2;
    public const double MinimumDurationSeconds = 1;
    public const double MaximumDurationSeconds = 3600;
    public const int MaximumNameLength = 500;
    public const int FirstPage = 1;
    public const int DefaultPageSize = 50;
    public const int MinimumPageSize = 1;
    public const int MaximumPageSize = 200;
    public const int MaximumPage = 100000;
    public const int MaximumSearchLength = 200;
    public const string JsonMediaType = "application/json";
}

public static class LyricsTrackMatchProtocol
{
    public const string RecordingIdentifierProperty = "recordingId";
    public const string IssuesProperty = "issues";
    public const string IssueTypeProperty = "Type";
    public const string CamelCaseIssueTypeProperty = "type";
    public const string TitleMismatch = "name_mismatch";
    public const string ArtistMismatch = "artist_mismatch";
    public const string EmptyIssues = "[]";
}

public sealed class LyricsOptions
{
    public const string Section = "Lyrics";
    public bool Enabled { get; set; } = true;
    public int DiscoveryBatchSize { get; set; } = 100;
    public TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan LeaseLifetime { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan MetadataRecheckInterval { get; set; } = TimeSpan.FromDays(1);
    public TimeSpan NotFoundRetryInterval { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan MinimumRequestInterval { get; } = TimeSpan.FromSeconds(1);
}

public sealed record LyricsTrack(Guid RecordingIdentifier, string Title, string Artist, string Album,
    int? DurationMilliseconds, bool HasNamingIssue)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtistCreditRevision { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Revision => System.Text.Json.JsonSerializer.Serialize(this);
    public double DurationSeconds => (DurationMilliseconds ?? LyricsProtocol.UnknownDurationMilliseconds) / (double)LyricsProtocol.MillisecondsPerSecond;
}

public sealed record LyricsContent(long ProviderIdentifier, string? PlainLyrics, string? SyncedLyrics, string? Lyricsfile, bool Instrumental);
public sealed record LyricsLease(RecordingLyrics Job, Guid Token);

public sealed class LyricsProviderException(string failureCode, TimeSpan? retryAfter = null) : Exception(failureCode)
{
    public string FailureCode { get; } = failureCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface ILyricsProvider
{
    Task<LyricsContent?> DownloadAsync(LyricsTrack track, CancellationToken cancellationToken);
}

public interface ILyricsRequestDelay
{
    Task WaitAsync(CancellationToken cancellationToken);
}

public interface ILyricsCatalog
{
    Task<LyricsTrack?> ReadAsync(Guid recordingIdentifier, CancellationToken cancellationToken);
}

public interface ILyricsIdentifiers
{
    Guid Create();
}

public interface ILyricsJobStore
{
    Task DiscoverAsync(int batchSize, CancellationToken cancellationToken);
    Task<LyricsLease?> ClaimAsync(TimeSpan leaseLifetime, CancellationToken cancellationToken);
    Task CompleteAsync(LyricsLease lease, LyricsTrack track, LyricsDownloadState state, LyricsContent? content,
        string failureCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);
    Task FailAsync(LyricsLease lease, string failureCode, DateTimeOffset nextAttemptAt, DateTimeOffset nextRequestAt, CancellationToken cancellationToken);
    Task ReleaseAsync(LyricsLease lease, DateTimeOffset nextRequestAt, CancellationToken cancellationToken);
}
