using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Models.Library;

namespace StreamingServiceServer.Business.Services.Recommendations;

public static class RecommendationPolicy
{
    public const string Route = "api/recommendations";
    public const string ExpiredSessionCode = "recommendation_session_expired";
    public const string InvalidRequestCode = "invalid_recommendation_request";
    public const string Daily = "daily";
    public const string SongRadio = "song-radio";
    public const string ArtistRadio = "artist-radio";
    public const string SmartShuffle = "smart-shuffle";
    public const string PlaylistRadio = "playlist-radio";
    public const string Calm = "calm";
    public const string Energy = "energy";
    public const string Instrumental = "instrumental";
    public const int PageSize = 20;
    public const int MaximumPageSize = 50;
    public const int DailySize = 40;
    public const int MaximumExclusions = 1500;
    public const int MaximumCursorCharacters = 131072;
    public const int MaximumSeeds = 8;
    public const int MaximumPerArtist = 2;
    public const int MaximumPerAlbum = 1;
    public const int CandidatePool = 500;
    public const int EmbeddingDimensions = 200;
    public const int QualifiedListenSeconds = 30;
    public const int RecentLimit = 100;
    public const int PolicyVersion = 1;
    public const int SupportedFeatureSchemaVersion = 1;
    public const double RandomVariation = 0.12;
    public const double DiversityPenalty = 0.12;
    public const double MinimumNorm = 1e-12;
    public const double MinimumStoredDescriptor = 0.5;
    public const double MaximumStoredDescriptor = 0.7310596;
    public const string SupportedEngine = "3.7.1";
    public const string SupportedProfile = "musicnn-v1";
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(6);
    public static readonly string[] Modes = [Daily, SongRadio, ArtistRadio, SmartShuffle, PlaylistRadio, Calm, Energy, Instrumental];
}

public sealed record RecommendationRequest(string Mode, Guid? RecordingId = null, Guid? ArtistId = null,
    Guid? PlaylistId = null, Guid? RandomSeed = null, int Count = RecommendationPolicy.PageSize,
    Guid[]? Exclude = null, string? ContinuationToken = null);
public sealed record RecommendationPage(IReadOnlyList<RecordingResponse> Tracks, string? ContinuationToken,
    string Explanation, string FeatureGeneration, string Mode);
public sealed record RecommendationCard(string Mode, string Title, string Description);
public sealed record RecommendationOverview(int AnalyzedTracks, IReadOnlyList<RecommendationCard> Cards);
public sealed record RecommendationSession(Guid UserId, RecommendationRequest Request, Guid RandomSeed,
    Guid[] Seeds, Guid[] Excluded, DateTimeOffset ExpiresAt, int PolicyVersion);
public sealed record SaveRecommendationPlaylist(string Name, Guid[] RecordingIds, Guid? DailyVersion = null);
public sealed record DailyMixResponse(Guid Version, DateOnly Day, IReadOnlyList<RecordingResponse> Tracks, Guid? SavedPlaylistId);
public sealed record RegenerateDailyMix(Guid ExpectedVersion);
public sealed record AudioRecommendationFeature(Guid RecordingId, Guid ReleaseId, Guid ArtistId, string ContentHash,
    string Cohort, float[] Vector, IReadOnlyDictionary<string, double> Descriptors, double Tempo, double Energy);
public sealed record AudioRecommendationSnapshot(string Generation, IReadOnlyList<AudioRecommendationFeature> Features);

public interface IAudioRecommendationFeatures
{
    Task<AudioRecommendationSnapshot> GetAsync(CancellationToken cancellationToken);
}
public interface IRecommendationCursorCodec
{
    string Encode(RecommendationSession session);
    RecommendationSession Decode(string token);
}
public interface IAudioRecommendations
{
    Task<RecommendationOverview> OverviewAsync(CancellationToken cancellationToken);
    Task<RecommendationPage> GenerateAsync(Guid userId, RecommendationRequest request, CancellationToken cancellationToken);
    Task<PlaylistResponse> SaveAsync(Guid userId, SaveRecommendationPlaylist request, CancellationToken cancellationToken);
    Task<DailyMixResponse> DailyAsync(Guid userId, Guid? expectedVersion, CancellationToken cancellationToken);
}
public sealed class RecommendationInputException(string message, string failureCode = RecommendationPolicy.InvalidRequestCode) : ArgumentException(message)
{
    public string FailureCode { get; } = failureCode;
}
public sealed class RecommendationConflictException() : Exception("This daily mix changed. Refresh before regenerating it.");

public interface IRecommendationEntropy { Guid Create(); }
public sealed class SystemRecommendationEntropy : IRecommendationEntropy { public Guid Create() => Guid.NewGuid(); }
